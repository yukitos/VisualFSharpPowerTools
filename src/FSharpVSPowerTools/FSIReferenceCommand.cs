﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.ComponentModel.Design;
using Microsoft.Win32;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using System.Linq;
using Microsoft.VisualStudio.Text.Editor;
using EnvDTE;
using EnvDTE80;
using System.Collections.Generic;
using System.IO;
using VSLangProj;
using System.Reflection;
using System.Windows;

namespace FSharpVSPowerTools
{
    public sealed class FSIReferenceCommand
    {
        public const uint cmdidAddReferenceInFSI = 0x100;
        public const string guidAddReferenceInFSICmdSetString = "8c9a49dd-2d34-4d18-905b-c557692980be";
        public static readonly Guid guidAddReferenceInFSICmdSet = new Guid(guidAddReferenceInFSICmdSetString);

        private DTE2 dte2;
        private BuildEvents buildEvents;
        private OleMenuCommandService mcs;
        private IVsUIShell shell;

        public FSIReferenceCommand(DTE2 dte2, OleMenuCommandService mcs, IVsUIShell shell)
        {
            this.dte2 = dte2;
            this.buildEvents = dte2.Events.BuildEvents;
            this.mcs = mcs;
            this.shell = shell;

            buildEvents.OnBuildDone += BuildEvents_OnBuildDone;
            Trace.WriteLine(string.Format(CultureInfo.CurrentCulture, "Entering constructor for: {0}", this.ToString()));
        }

        void BuildEvents_OnBuildDone(vsBuildScope Scope, vsBuildAction Action)
        {
            foreach (Project project in dte2.Solution.Projects)
            {
                if (this.IsCurrentProjectFSharp(project) && 
                    this.ContainsRefScript(project))
                {
                    this.GenerateFile(project);
                }
            }
        }

        private bool ContainsRefScript(Project project)
        {
            var scriptItem = project.ProjectItems.Item("Scripts");
            if (scriptItem == null)
                return false;

            if (scriptItem.ProjectItems != null && scriptItem.ProjectItems.Count > 0)
            {
                for (int i = 0; i < scriptItem.ProjectItems.Count; i++)
                {
                    if (scriptItem.ProjectItems.Item(i + 1).Name.Contains("load-refs"))
                        return true;
                }
            }

            return false;
        }

        private Project GetActiveProject()
        {
            var dte = dte2 as DTE;
            return GetActiveProject(dte);
        }

        private Project GetActiveProject(DTE dte)
        {
            Project activeProject = null;

            Array activeSolutionProjects = dte.ActiveSolutionProjects as Array;
            if (activeSolutionProjects != null && activeSolutionProjects.Length > 0)
            {
                activeProject = activeSolutionProjects.GetValue(0) as Project;
            }

            return activeProject;
        }

        private bool IsCurrentProjectFSharp(Project proj)
        {
            var result = proj.Kind.ToLower() == "{f2a71f9b-5d33-465a-a702-920d77279786}";
            return result;
        }

        private void AddFileToActiveProject(Project project, string fileName, string content)
        {
            var proj = project;
            var subfolderName = "Scripts";
            if (this.IsCurrentProjectFSharp(proj))
            {
                // Create Script folder
                var projectFolder = this.GetProjectFolder(proj);
                var scriptFolder = Path.Combine(projectFolder, subfolderName);
                if (!Directory.Exists(scriptFolder))
                {
                    Directory.CreateDirectory(scriptFolder);
                }

                var path = scriptFolder;
                var textFile = Path.Combine(path, fileName);
                using (var writer = File.CreateText(textFile))
                {
                    writer.Write(content);
                }
                var projectFolderScript = proj.ProjectItems.Item(subfolderName) != null ?
                        proj.ProjectItems.Item(subfolderName) : proj.ProjectItems.AddFolder(subfolderName);
                projectFolderScript.ProjectItems.AddFromFile(textFile);
                proj.Save();
            }
        }

        private Dictionary<string, string> GetOutputFileFullPathes(VSLangProj.Reference reference)
        {
            var sourceProject = reference.GetType().GetProperty("SourceProject");
            if (sourceProject==null)
                return null;

            var dict = new Dictionary<string, string>();
            var project = sourceProject.GetValue(reference) as Project;
            var result = this.GetProjectOuputs(project);
            return result;            
        }

        private bool IsReferenceProject(VSLangProj.Reference reference)
        {
            var sourceProject = reference.GetType().GetProperty("SourceProject");
            var result = sourceProject != null && sourceProject.GetValue(reference)!=null;
            return result;
        }

        private string GenerateFileContent(Project project, string tag)
        {
            var excludingList = new string[] { "FSharp.Core", "mscorlib" };

            var list = new List<string>();
            var projectRefList = new List<string>();

            if (project.Object is VSProject)
            {
                VSProject vsproject = (VSProject)project.Object;
                for (int i = 0; i < vsproject.References.Count; i++)
                {
                    var reference = vsproject.References.Item(i + 1);
                    if (excludingList.Contains(reference.Name))
                        continue;

                    if (this.IsReferenceProject(reference))
                    {
                        var outputFilePath = this.GetOutputFileFullPathes(reference);
                        if (outputFilePath != null && outputFilePath.ContainsKey(tag))
                        {
                            projectRefList.Add(outputFilePath[tag]);
                        }
                    }
                    else
                    {
                        var fullPath = reference.Path;
                        if (File.Exists(fullPath))
                            list.Add(reference.Path);
                    }
                }
            }

            list = list.Select(n => String.Format("#r @\"{0}\"", n)).ToList();
            projectRefList = projectRefList.Select(n => String.Format("#r @\"{0}\"", n)).ToList();
            var result = String.Format("// Warning: Generated file, your change could be lost when new file is generated. \r\n{0}\r\n\r\n{1}",
                            String.Join("\r\n", list),
                            String.Join("\r\n", projectRefList));
            return result;
        }

        private string GetProjectFolder(Project project)
        {
            var projectPath = project.Properties.Item("FullPath").Value.ToString();
            return projectPath;
        }

        private Dictionary<string, string> GetProjectOuputs(Project project)
        {
            var dict = new Dictionary<string, string>();
            var projectPath = this.GetProjectFolder(project);
            var outputFileName = project.Properties.Item("OutputFileName").Value.ToString();
            for (int i = 0; i < project.ConfigurationManager.Count; i++)
            {
                var config = project.ConfigurationManager.Item(i + 1);
                var outputPath = config.Properties.Item("OutputPath").Value.ToString();
                var p = Path.Combine(Path.Combine(projectPath, outputPath), outputFileName);
                dict.Add(config.ConfigurationName, p);
            }
            return dict;
        }

        private string GenerateLoadScriptContent(Project project, string scriptFile, string tag)
        {
            var projectfolder = Path.Combine(this.GetProjectFolder(project), "Scripts");
            var load = String.Format("#load @\"{0}\"", Path.Combine(projectfolder, scriptFile));
            var outputs = this.GetProjectOuputs(project);
            if (outputs.ContainsKey(tag))
            {
                var output = outputs[tag];
                var result = String.Format("#r @\"{0}\"\r\n", output);
                return String.Format("{0}\r\n{1}", load, result);
            }
            else
            {
                return String.Format("{0}\r\n", load);
            }
        }

        private void GenerateFile(Project project)
        {
            if (project == null)
                return;            

            var outputs = this.GetProjectOuputs(project);
            foreach (var output in outputs)
            {
                var tag = output.Key;
                var fileName = tag == "Debug" ? "load-refs.fsx" : String.Format("load-refs-{0}.fsx", tag);
                var content = this.GenerateFileContent(project, tag);
                this.AddFileToActiveProject(project, fileName, content);
                content = this.GenerateLoadScriptContent(project, fileName, tag);
                fileName = tag == "Debug" ? "load-project.fsx" : String.Format("load-project-{0}.fsx", tag);
                this.AddFileToActiveProject(project, fileName, content);
            }
        }

        private List<string> GetReferences(Project project)
        {
            var excludingList = new string[] { "FSharp.Core", "mscorlib" };

            var list = new List<string>();

            if (project.Object is VSProject)
            {
                VSProject vsproject = (VSProject)project.Object;
                for (int i = 0; i < vsproject.References.Count; i++)
                {
                    var reference = vsproject.References.Item(i+1);                    
                    if (excludingList.Contains(reference.Name))
                        continue;

                    var fullPath = reference.Path;
                    if (File.Exists(fullPath))
                        list.Add(reference.Path);
                }
            }

            return list;
        }

        public void SetupCommands()
        {
            if (null != mcs)
            {
                // Create the command for the menu item.
                CommandID menuCommandID = new CommandID(guidAddReferenceInFSICmdSet, (int)cmdidAddReferenceInFSI);
                MenuCommand menuItem = new MenuCommand(AddReferenceInFSI, menuCommandID);
                mcs.AddCommand(menuItem);
            }
        }

        /// <summary>
        /// This function is the callback used to execute a command when the a menu item is clicked.
        /// See the Initialize method to see how the menu item is associated to this function using
        /// the OleMenuCommandService service and the MenuCommand class.
        /// </summary>
        private void AddReferenceInFSI(object sender, EventArgs e)
        {
            IVsWindowFrame frame;
            Guid guid = new Guid("dee22b65-9761-4a26-8fb2-759b971d6dfc");
            shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref guid, out frame);
            if (frame != null)
            {
                //if (frame.IsVisible() != 0)
                {
                    frame.Show();
                }

                var project = GetActiveProject();
                var l = GetReferences(project);

                var t = frame.GetType();
                var mi = t.GetProperty("FrameView", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var frameView = mi.GetValue(frame);
                var v = frameView.GetType().GetProperty("Content").GetValue(frameView) as DependencyObject;
                var content = v.GetType().GetProperty("Content").GetValue(v) as DependencyObject;
                var content2 = content.GetType().GetProperty("Content").GetValue(content);
                var content3 = content2.GetType().GetProperty("Content").GetValue(content2);
                var content4 = content3.GetType().GetProperty("TextView").GetValue(content3);
                var wpfView = content4 as IWpfTextView;
                var textBuffer = wpfView.TextBuffer;                
                using (var edit = textBuffer.CreateEdit())
                {
                    var line = wpfView.Caret.ContainingTextViewLine;
                    var pos = line.End.Position;

                    string resultString = "\r\n";
                    foreach (var item in l)
                    {
                        resultString += String.Format("#r @\"{0}\"\r\n", item);
                    }

                    edit.Insert(pos, resultString.TrimEnd() + ";;");
                    edit.Apply();
                }

                // Generate script files
                if (this.IsCurrentProjectFSharp(project))
                {
                    this.GenerateFile(project);
                }
            }
            else
            {
                Guid clsid = Guid.Empty;
                int result;
                ErrorHandler.ThrowOnFailure(shell.ShowMessageBox(
                           0,
                           ref clsid,
                           "",
                           string.Format(CultureInfo.CurrentCulture, "Please open FSI.", this.ToString()),
                           string.Empty,
                           0,
                           OLEMSGBUTTON.OLEMSGBUTTON_OK,
                           OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST,
                           OLEMSGICON.OLEMSGICON_INFO,
                           0,        // false
                           out result));
            }
        }

    }
}
