﻿// Copyright (c) 2019 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.Decompiler.Solution
{
	/// <summary>
	/// A helper class that can write a Visual Studio Solution file for the provided projects.
	/// </summary>
	public static class SolutionCreator
	{
		static readonly XNamespace NonSDKProjectFileNamespace = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");

		/// <summary>
		/// Writes a solution file to the specified <paramref name="targetFile"/>.
		/// Also fixes intra-solution project references in the project files.
		/// </summary>
		/// <param name="targetFile">The full path of the file to write.</param>
		/// <param name="projects">The projects contained in this solution.</param>
		/// 
		/// <exception cref="ArgumentException">Thrown when <paramref name="targetFile"/> is null or empty.</exception>
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="projects"/> is null.</exception>
		/// <exception cref="InvalidOperationException">Thrown when <paramref name="projects"/> contains no items.</exception>
		public static void WriteSolutionFile(string targetFile, List<ProjectItem> projects)
		{
			if (string.IsNullOrWhiteSpace(targetFile))
			{
				throw new ArgumentException("The target file cannot be null or empty.", nameof(targetFile));
			}

			if (projects == null)
			{
				throw new ArgumentNullException(nameof(projects));
			}

			if (!projects.Any())
			{
				throw new InvalidOperationException("At least one project is expected.");
			}

			using (var writer = new StreamWriter(targetFile))
			{
				WriteSolutionFile(writer, projects, targetFile);
			}

			FixAllProjectReferences(projects);
		}

		static void WriteSolutionFile(TextWriter writer, List<ProjectItem> projects, string solutionFilePath)
		{
			WriteHeader(writer);
			WriteProjects(writer, projects, solutionFilePath);

			writer.WriteLine("Global");

			var platforms = WriteSolutionConfigurations(writer, projects);
			WriteProjectConfigurations(writer, projects, platforms);

			writer.WriteLine("\tGlobalSection(SolutionProperties) = preSolution");
			writer.WriteLine("\t\tHideSolutionNode = FALSE");
			writer.WriteLine("\tEndGlobalSection");

			writer.WriteLine("EndGlobal");
		}

		private static void WriteHeader(TextWriter writer)
		{
			writer.WriteLine("Microsoft Visual Studio Solution File, Format Version 12.00");
			writer.WriteLine("# Visual Studio 14");
			writer.WriteLine("VisualStudioVersion = 14.0.24720.0");
			writer.WriteLine("MinimumVisualStudioVersion = 10.0.40219.1");
		}

		static void WriteProjects(TextWriter writer, List<ProjectItem> projects, string solutionFilePath)
		{
			foreach (var project in projects)
			{
				var projectRelativePath = GetRelativePath(solutionFilePath, project.FilePath);
				var typeGuid = project.TypeGuid.ToString("B").ToUpperInvariant();
				var projectGuid = project.Guid.ToString("B").ToUpperInvariant();

				writer.WriteLine($"Project(\"{typeGuid}\") = \"{project.ProjectName}\", \"{projectRelativePath}\", \"{projectGuid}\"");
				writer.WriteLine("EndProject");
			}
		}

		static List<string> WriteSolutionConfigurations(TextWriter writer, List<ProjectItem> projects)
		{
			var platforms = projects.GroupBy(p => p.PlatformName).Select(g => g.Key).ToList();

			platforms.Sort();

			writer.WriteLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
			foreach (var platform in platforms)
			{
				writer.WriteLine($"\t\tDebug|{platform} = Debug|{platform}");
			}

			foreach (var platform in platforms)
			{
				writer.WriteLine($"\t\tRelease|{platform} = Release|{platform}");
			}

			writer.WriteLine("\tEndGlobalSection");

			return platforms;
		}

		static void WriteProjectConfigurations(
			TextWriter writer,
			List<ProjectItem> projects,
			List<string> solutionPlatforms)
		{
			writer.WriteLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");

			foreach (var project in projects)
			{
				var projectGuid = project.Guid.ToString("B").ToUpperInvariant();

				foreach (var platform in solutionPlatforms)
				{
					writer.WriteLine($"\t\t{projectGuid}.Debug|{platform}.ActiveCfg = Debug|{project.PlatformName}");
					writer.WriteLine($"\t\t{projectGuid}.Debug|{platform}.Build.0 = Debug|{project.PlatformName}");
				}

				foreach (var platform in solutionPlatforms)
				{
					writer.WriteLine($"\t\t{projectGuid}.Release|{platform}.ActiveCfg = Release|{project.PlatformName}");
					writer.WriteLine($"\t\t{projectGuid}.Release|{platform}.Build.0 = Release|{project.PlatformName}");
				}
			}

			writer.WriteLine("\tEndGlobalSection");
		}

		static void FixAllProjectReferences(List<ProjectItem> projects)
		{
			var projectsMap = projects.ToDictionary(
				p => p.ProjectName,
				p => p);

			foreach (var project in projects)
			{
				XDocument projectDoc = XDocument.Load(project.FilePath);

				if (projectDoc.Root?.Name.LocalName != "Project")
				{
					throw new InvalidOperationException(
						$"The file {project.FilePath} is not a valid project file, " +
						$"no <Project> at the root; could not fix project references.");
				}

				// sdk style projects don't use a namespace for the elements,
				// but we still need to use the namespace for non-sdk style projects.
				var sdkStyle = projectDoc.Root.Attribute("Sdk") != null;
				var itemGroupTagName = sdkStyle ? "ItemGroup" : NonSDKProjectFileNamespace + "ItemGroup";
				var referenceTagName = sdkStyle ? "Reference" : NonSDKProjectFileNamespace + "Reference";

				var referencesItemGroups = projectDoc.Root
					.Elements(itemGroupTagName)
					.Where(e => e.Elements(referenceTagName).Any())
					.ToList();

				foreach (var itemGroup in referencesItemGroups)
				{
					FixProjectReferences(project.FilePath, itemGroup, projectsMap, sdkStyle);
				}

				projectDoc.Save(project.FilePath);
			}
		}

		static void FixProjectReferences(string projectFilePath, XElement itemGroup,
			Dictionary<string, ProjectItem> projects, bool sdkStyle)
		{

			XName GetElementName(string name) => sdkStyle ? name : NonSDKProjectFileNamespace + name;

			var referenceTagName = GetElementName("Reference");
			var projectReferenceTagName = GetElementName("ProjectReference");

			foreach (var item in itemGroup.Elements(referenceTagName).ToList())
			{
				var assemblyName = item.Attribute("Include")?.Value;
				if (assemblyName != null && projects.TryGetValue(assemblyName, out var referencedProject))
				{
					item.Remove();

					var projectReference = new XElement(
						projectReferenceTagName,
						new XAttribute("Include", GetRelativePath(projectFilePath, referencedProject.FilePath)));

					// SDK-style projects do not use the <Project> and <Name> elements for project references.
					// (Instead, those get read from the .csproj file in "Include".)
					if (!sdkStyle)
					{
						projectReference.Add(
							// no ToUpper() for uuids, most Microsoft tools seem to emit them in lowercase
							// (no .ToLower() as .ToString("B") already outputs lowercase)
							new XElement(NonSDKProjectFileNamespace + "Project", referencedProject.Guid.ToString("B")),
							new XElement(NonSDKProjectFileNamespace + "Name", referencedProject.ProjectName));
					}

					itemGroup.Add(projectReference);
				}
			}
		}

		static string GetRelativePath(string fromFilePath, string toFilePath)
		{
			Uri fromUri = new Uri(fromFilePath);
			Uri toUri = new Uri(toFilePath);

			if (fromUri.Scheme != toUri.Scheme)
			{
				return toFilePath;
			}

			Uri relativeUri = fromUri.MakeRelativeUri(toUri);
			string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

			if (string.Equals(toUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
			{
				relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
			}

			return relativePath;
		}
	}
}
