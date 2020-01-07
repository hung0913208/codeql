﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace Semmle.BuildAnalyser
{
    /// <summary>
    /// A reference to a particular version of a particular package.
    /// </summary>
    class PackageReference
    {
        public PackageReference(string include, string version) { 
            Include = include;
            Version = version;
        }
        public string Include, Version;

        public override string ToString() => $"Include={Include}, Version={Version}";
    }

    enum ProjectFileType
    {
        MsBuildProject,
        DotNetProject,
        OtherProject
    }

    interface IProjectFile
    {
        IEnumerable<IProjectFile> ProjectReferences { get; }

        IEnumerable<PackageReference> Packages { get; }

        IEnumerable<string> References { get; }

        IEnumerable<FileInfo> Sources { get; }

        IEnumerable<string> TargetFrameworks { get; }
    }

    class NetCoreProjectFile : IProjectFile
    {
        FileInfo path;
        XmlDocument doc;
        XmlElement root;

        public NetCoreProjectFile(FileInfo path)
        {
            this.path = path;
            doc = new XmlDocument();
            doc.Load(path.FullName);
            root = doc.DocumentElement;
        }

        public IEnumerable<IProjectFile> ProjectReferences => throw new System.NotImplementedException();

        public IEnumerable<PackageReference> Packages
        {
            get
            {
                var packages = root.SelectNodes("/Project/ItemGroup/PackageReference");
                return packages.NodeList().
                    Select(r =>
                    new PackageReference(r.Attributes.GetNamedItem("Include").Value, r.Attributes.GetNamedItem("Version").Value));
            }
        }

        public IEnumerable<string> References => throw new System.NotImplementedException();

        public IEnumerable<FileInfo> Sources
        {
            get
            {
                return path.Directory.GetFiles("*.cs", SearchOption.AllDirectories);
            }
        }

        public IEnumerable<string> TargetFrameworks => throw new System.NotImplementedException();
    }

    /// <summary>
    /// Represents a .csproj file and reads information from it.
    /// </summary>
    class CsProjFile
    {
        public string Filename { get; }

        public string Directory => Path.GetDirectoryName(Filename);

        /// <summary>
        /// Reads the .csproj file.
        /// </summary>
        /// <param name="filename">The .csproj file.</param>
        public CsProjFile(FileInfo filename)
        {
            Filename = filename.FullName;

            try
            {
                // This can fail if the .csproj is invalid or has
                // unrecognised content or is the wrong version.
                // This currently always fails on Linux because
                // Microsoft.Build is not cross platform.
                ReadMsBuildProject(filename);
            }
            catch  // lgtm[cs/catch-of-all-exceptions]
            {
                // There was some reason why the project couldn't be loaded.
                // Fall back to reading the Xml document directly.
                // This method however doesn't handle variable expansion.
                ReadProjectFileAsXml(filename);
            }
        }

        /// <summary>
        /// Read the .csproj file using Microsoft Build.
        /// This occasionally fails if the project file is incompatible for some reason,
        /// and there seems to be no way to make it succeed. Fails on Linux.
        /// </summary>
        /// <param name="filename">The file to read.</param>
        public void ReadMsBuildProject(FileInfo filename)
        {
            var msbuildProject = new Microsoft.Build.Execution.ProjectInstance(filename.FullName);

            references = msbuildProject.
                Items.
                Where(item => item.ItemType == "Reference").
                Select(item => item.EvaluatedInclude).
                ToArray();

            csFiles = msbuildProject.Items
                .Where(item => item.ItemType == "Compile")
                .Select(item => item.GetMetadataValue("FullPath"))
                .Where(fn => fn.EndsWith(".cs"))
                .ToArray();
        }

        string[] targetFrameworks = new string[0];

        /// <summary>
        /// Reads the .csproj file directly as XML.
        /// This doesn't handle variables etc, and should only used as a
        /// fallback if ReadMsBuildProject() fails.
        /// </summary>
        /// <param name="filename">The .csproj file.</param>
        public void ReadProjectFileAsXml(FileInfo filename)
        {
            var projFile = new XmlDocument();
            var mgr = new XmlNamespaceManager(projFile.NameTable);
            mgr.AddNamespace("msbuild", "http://schemas.microsoft.com/developer/msbuild/2003");
            projFile.Load(filename.FullName);
            var projDir = filename.Directory;
            var root = projFile.DocumentElement;

            // Figure out if it's dotnet core

            bool netCoreProjectFile = root.GetAttribute("Sdk") == "Microsoft.NET.Sdk";

            if(netCoreProjectFile)
            {
                var frameworksNode = root.SelectNodes("/Project/PropertyGroup/TargetFrameworks").NodeList().Concat(
                    root.SelectNodes("/Project/PropertyGroup/TargetFramework").NodeList()).Select(node => node.InnerText);

                targetFrameworks = frameworksNode.SelectMany(node => node.Split(";")).ToArray();

                var relativeCsIncludes2 =
                    root.SelectNodes("/Project/ItemGroup/Compile/@Include", mgr).
                    NodeList().
                    Select(node => node.Value).
                    ToArray();

                var explicitCsFiles = relativeCsIncludes2.
                    Select(cs => Path.DirectorySeparatorChar == '/' ? cs.Replace("\\", "/") : cs).
                    Select(f => Path.GetFullPath(Path.Combine(projDir.FullName, f)));

                var additionalCsFiles = System.IO.Directory.GetFiles(Directory, "*.cs", SearchOption.AllDirectories);

                csFiles = explicitCsFiles.Concat(additionalCsFiles).ToArray();

                references = new string[0];
                return;
            }

            references =
                root.SelectNodes("/msbuild:Project/msbuild:ItemGroup/msbuild:Reference/@Include", mgr).
                NodeList().
                Select(node => node.Value).
                ToArray();

            var relativeCsIncludes =
                root.SelectNodes("/msbuild:Project/msbuild:ItemGroup/msbuild:Compile/@Include", mgr).
                NodeList().
                Select(node => node.Value).
                ToArray();

            csFiles = relativeCsIncludes.
                Select(cs => Path.DirectorySeparatorChar == '/' ? cs.Replace("\\", "/") : cs).
                Select(f => Path.GetFullPath(Path.Combine(projDir.FullName, f))).
                ToArray();
        }

        string[] references;
        string[] csFiles;

        /// <summary>
        /// The list of references as a list of assembly IDs.
        /// </summary>
        public IEnumerable<string> References => references;

        public IEnumerable<string> TargetFrameworks => targetFrameworks;

        /// <summary>
        /// The list of C# source files in full path format.
        /// </summary>
        public IEnumerable<string> Sources => csFiles;
    }

    static class XmlNodeHelper
    {
        /// <summary>
        /// Helper to convert an XmlNodeList into an IEnumerable.
        /// This allows it to be used with Linq.
        /// </summary>
        /// <param name="list">The list to convert.</param>
        /// <returns>A more useful data type.</returns>
        public static IEnumerable<XmlNode> NodeList(this XmlNodeList list)
        {
            foreach (var i in list)
                yield return i as XmlNode;
        }
    }
}
