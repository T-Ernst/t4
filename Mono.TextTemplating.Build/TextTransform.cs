// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using MessagePack;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Mono.TextTemplating.Build
{
	// parameter values:
	// VS TextTemplatingFileGenerator tool: all MSBuild variables from the project, but not T4ParameterValue items
	// VS MSBuild targets: T4ParameterValue items, but not arbitrary MSBuild properties (ironically)
	// maybe we should add some common ones by default?
	public class TextTransform : Task
	{
		public TextTransform () : base (Messages.ResourceManager) { }

		public string DefaultNamespace { get; set; }
		public ITaskItem[] PreprocessTemplates { get; set; }
		public ITaskItem[] TransformTemplates { get; set; }
		public ITaskItem[] IncludePaths { get; set; }
		public ITaskItem[] DirectiveProcessors { get; set; }
		public ITaskItem[] AssemblyReferences { get; set; }
		public ITaskItem[] ReferencePaths { get; set; }

		public ITaskItem[] ParameterValues { get; set; }

		public bool PreprocessOnly { get; set; }
		public bool UseLegacyPreprocessingMode { get; set; }
		public bool TransformOutOfDateOnly { get; set; }

		public string PreprocessTargetRuntimeIdentifier { get; set; }

		[Required]
		public string IntermediateDirectory { get; set; }

		[Required]
		public string ProjectDirectory { get; set; }

		[Output]
		public ITaskItem[] RequiredAssemblies { get; set; }

		[Output]
		public ITaskItem[] TransformTemplateOutput { get; set; }

		[Output]
		public ITaskItem[] PreprocessedTemplateOutput { get; set; }

		public override bool Execute ()
		{
			bool success = true;

			Directory.CreateDirectory (IntermediateDirectory);
			Directory.CreateDirectory (ProjectDirectory);

			string buildStateFilename = Path.Combine (IntermediateDirectory, "t4-build-state.msgpack");

			var msgPackOptions = MessagePackSerializerOptions.Standard
				.WithAllowAssemblyVersionMismatch (false)
				.WithCompression (MessagePackCompression.Lz4BlockArray)
				.WithSecurity (MessagePackSecurity.TrustedData);


			TemplateBuildState previousBuildState = null;
			if (TransformOutOfDateOnly) {
				previousBuildState = LoadBuildState (buildStateFilename, msgPackOptions);
				if (previousBuildState != null) {
					Log.LogMessageFromResources (MessageImportance.Low, nameof (Messages.LoadedStateFile), buildStateFilename);
				}
			}

			var buildState = new TemplateBuildState {
				IntermediateDirectory = IntermediateDirectory,
				DefaultNamespace = DefaultNamespace,
				PreprocessTargetRuntimeIdentifier = PreprocessTargetRuntimeIdentifier
			};

			success &= AddParameters (buildState);
			success &= AddDirectiveProcessors (buildState);

			if (!success) {
				return false;
			}

			if (IncludePaths != null) {
				buildState.IncludePaths = new List<string> (IncludePaths.Select (i => i.ItemSpec));
			}

			if (ReferencePaths != null) {
				buildState.ReferencePaths = new List<string> (ReferencePaths.Select (i => i.ItemSpec));
			}

			if (AssemblyReferences != null) {
				buildState.AssemblyReferences = new List<string> (AssemblyReferences.Select (i => i.ItemSpec));
			}

			if (PreprocessTemplates != null) {
				buildState.PreprocessTemplates = new List<TemplateBuildState.PreprocessedTemplate> ();
				foreach (var ppt in PreprocessTemplates) {
					string inputFile = ppt.ItemSpec;
					string outputFile;

					// Metadata only supported for legacy processing.
					string extensionOverride = null;
					if (UseLegacyPreprocessingMode) {
						outputFile = GetOutputPathViaMetadata (ppt, ".cs", out extensionOverride);
					} else {
						outputFile = Path.Combine (IntermediateDirectory, Path.ChangeExtension (inputFile, ".cs"));
					}

					buildState.PreprocessTemplates.Add (new TemplateBuildState.PreprocessedTemplate {
						InputFile = inputFile,
						OutputFile = outputFile,
						Namespace = CalculateNamespace (outputFile),
						ExtensionOverride = extensionOverride
					});
				}
			}

			if (TransformTemplates != null) {
				buildState.TransformTemplates = new List<TemplateBuildState.TransformTemplate> ();
				foreach (var tt in TransformTemplates) {
					string inputFile = tt.ItemSpec;

					var outputFile = GetOutputPathViaMetadata (tt, ".txt", out var extensionOverride);

					buildState.TransformTemplates.Add (new TemplateBuildState.TransformTemplate {
						InputFile = inputFile,
						OutputFile = outputFile,
						ExtensionOverride = extensionOverride
					});
				}
			}

			TextTransformProcessor.Process (Log, previousBuildState, buildState, PreprocessOnly);

			if (buildState.TransformTemplates != null) {
				TransformTemplateOutput = new ITaskItem[buildState.TransformTemplates.Count];
				for (int i = 0; i < buildState.TransformTemplates.Count; i++) {
					var template = buildState.TransformTemplates[i];
					TransformTemplateOutput[i] = ConstructOutputItem (template.OutputFile, template.InputFile, template.Dependencies);
				}
			}

			if (buildState.PreprocessTemplates != null) {
				PreprocessedTemplateOutput = new ITaskItem[buildState.PreprocessTemplates.Count];
				for (int i = 0; i < buildState.PreprocessTemplates.Count; i++) {
					var template = buildState.PreprocessTemplates[i];
					PreprocessedTemplateOutput[i] = ConstructOutputItem (template.OutputFile, template.InputFile, template.Dependencies);
				}
			}

			//TODO
			//RequiredAssemblies
			//settings.Debug
			//settings.Log

			SaveBuildState (buildState, buildStateFilename, msgPackOptions);

			//var stateJson = MessagePackSerializer.ConvertToJson (File.ReadAllBytes (buildStateFilename), msgPackOptions);

			return success;
		}

		string GetOutputPathViaMetadata (ITaskItem taskItem, string extensionDefault, out string extensionOverride)
		{
			var inputFile = taskItem.ItemSpec;
			extensionOverride = null;
			if (!taskItem.TryGetMetadata ("OutputFileName", out var outputFile)) {
				var name = Path.GetFileNameWithoutExtension (inputFile);
				outputFile = Path.ChangeExtension (name, extensionDefault);
			} else {
				extensionOverride = Path.GetExtension (outputFile);
			}

			// If set, it is relative to the ProjectDirectory.
			if (taskItem.TryGetMetadata ("OutputDirectory", out var outputDirectory)) {
				outputFile = Path.Combine (ProjectDirectory, outputDirectory, outputFile);
			} else if (taskItem.TryGetMetadata ("OutputFilePath", out outputDirectory)) {
				outputFile = Path.Combine (ProjectDirectory, outputDirectory, outputFile);
			} else { // otherwise use the same directory as the template.
				var parentDir = GetDirectoryFullPath (inputFile);
				outputFile = Path.Combine (parentDir, outputFile);
			}

			return outputFile;
		}

		static TaskItem ConstructOutputItem (string outputFile, string inputFile, List<string> itemDependencies)
		{
			var item = new TaskItem (outputFile);
			item.SetMetadata ("InputFile", inputFile);

			if (itemDependencies?.Count > 0) {
				item.SetMetadata ("Dependencies", string.Join (";", itemDependencies));
			}

			return item;
		}

		static string GetDirectoryFullPath (string inputFile)
		{
			var fullPath = Path.GetFullPath (inputFile);
			return Path.GetDirectoryName (fullPath);
		}

		static string GetRelativePath (string relativeTo, string path)
		{
#if !NETCOREAPP2_1_OR_GREATER
			// Implement a basic version of GetRelativePath for .NET Framework
			Uri relativeToUri = new Uri(relativeTo);
			Uri pathUri = new Uri(path);
			return Uri.UnescapeDataString(relativeToUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
#else
			return Path.GetRelativePath (relativeTo, path);
#endif
		}

		bool AddParameters (TemplateBuildState buildState)
		{
			bool success = true;

			if (ParameterValues == null) {
				return true;
			}

			buildState.Parameters = new List<TemplateBuildState.Parameter> ();

			foreach (var par in ParameterValues) {
				string paramName = par.ItemSpec;
				string processorName, directiveName, paramVal;

				if (TemplateGenerator.TryParseParameter (paramName, out processorName, out directiveName, out string parsedName, out paramVal)) {
					paramName = parsedName;
				}

				// metadata overrides encoded values. todo: warn when this happens?
				if (par.TryGetMetadata ("Value", out string valueMetadata)) {
					paramVal = valueMetadata;
				}

				if (par.TryGetMetadata ("Processor", out string processorMetadata)) {
					processorName = processorMetadata;
				}

				if (par.TryGetMetadata ("Directive", out string directiveMetadata)) {
					directiveName = directiveMetadata;
				}

				if (paramVal is null) {
					Log.LogWarningFromResources (nameof (Messages.ArgumentNoValue), par);
					success = false;
					continue;
				}

				buildState.Parameters.Add (new TemplateBuildState.Parameter {
					Processor = processorName,
					Directive = directiveName,
					Name = paramName,
					Value = paramVal
				});
			}

			return success;
		}

		bool AddDirectiveProcessors (TemplateBuildState buildState)
		{
			if (DirectiveProcessors == null) {
				return true;
			}

			buildState.DirectiveProcessors = new List<TemplateBuildState.DirectiveProcessor> ();

			bool hasErrors = false;

			foreach (var dirItem in DirectiveProcessors) {

				var name = dirItem.ItemSpec;
				string className = null, assembly = null;

				if (name.IndexOf ('!') > -1) {
					var split = name.Split ('!');
					if (split.Length != 3) {
						Log.LogErrorFromResources (nameof (Messages.DirectiveProcessorDoesNotHaveThreeValues), name);
						return false;
					}
					//empty values for these are fine; they may get set through metadata
					name = split[0];
					className = split[1];
					assembly = split[2];
				}

				if (dirItem.TryGetMetadata ("Class", out string classMetadata)) {
					className = classMetadata;
				}

				if (dirItem.TryGetMetadata ("Codebase", out string codebaseMetadata)) {
					assembly = codebaseMetadata;
				}

				if (dirItem.TryGetMetadata ("Assembly", out string assemblyMetadata)) {
					assembly = assemblyMetadata;
				}

				if (string.IsNullOrEmpty (className)) {
					Log.LogErrorFromResources (nameof (Messages.DirectiveProcessorNoClass), name);
					hasErrors = true;
				}

				if (string.IsNullOrEmpty (assembly)) {
					Log.LogErrorFromResources (nameof (Messages.DirectiveProcessorNoAssembly), name);
					hasErrors = true;
				}

				buildState.DirectiveProcessors.Add (new TemplateBuildState.DirectiveProcessor {
					Name = name,
					Class = className,
					Assembly = assembly
				});
			}

			return !hasErrors;
		}

		string CalculateNamespace (string outputFile)
		{
			string relativePath = GetRelativePath (UseLegacyPreprocessingMode ? ProjectDirectory : IntermediateDirectory, outputFile);
			string namespacePath = Path.GetDirectoryName (relativePath).Replace (Path.DirectorySeparatorChar, '.');
			return string.IsNullOrEmpty (namespacePath) ? DefaultNamespace : $"{DefaultNamespace}.{namespacePath}";
		}

		TemplateBuildState LoadBuildState (string filePath, MessagePackSerializerOptions options)
		{
			if (!File.Exists (filePath)) {
				return null;
			}

			try {
				using var stream = File.OpenRead (filePath);

				var state = MessagePackSerializer.Deserialize<TemplateBuildState> (stream, options);

				if (state.FormatVersion != TemplateBuildState.CurrentFormatVersion) {
					Log.LogMessageFromResources (MessageImportance.Low, nameof (Messages.BuildStateFormatChanged));
				}

				return state;
			}
			catch (Exception ex) {
				// show a meaningful error message without internal details
				Log.LogWarningFromResources (nameof (Messages.BuildStateLoadFailed));
				// log a stack trace so it can be reported
				Log.LogMessageFromResources (MessageImportance.Normal, nameof (Messages.InternalException), ex);
			}

			return null;
		}

		void SaveBuildState (TemplateBuildState buildState, string filePath, MessagePackSerializerOptions options)
		{
			try {
				using var stream = File.Create (filePath);
				MessagePackSerializer.Serialize (stream, buildState, options);
			}
			catch (Exception ex) {
				// show a meaningful error message without internal details
				Log.LogWarningFromResources (nameof (Messages.BuildStateSaveFailed));
				// log a stack trace so it can be reported
				Log.LogMessageFromResources (MessageImportance.Normal, nameof (Messages.InternalException), ex);
				try {
					if (File.Exists (filePath)) {
						File.Delete (filePath);
					}
				}
				catch {
				}
			}
		}
	}

	static class TaskItemExtensions
	{
		public static bool TryGetMetadata (this ITaskItem item, string name, out string value)
		{
			var potentialValue = item.GetMetadata (name);
			if (potentialValue?.Length > 0) {
				value = potentialValue;
				return true;
			}

			value = null;
			return false;
		}
	}
}
