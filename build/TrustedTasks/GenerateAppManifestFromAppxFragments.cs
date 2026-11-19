// Copyright (c) Microsoft Corporation. Licensed under the MIT License.
// The Execute body below is the unchanged XML-normalized Code fragment from
// Microsoft.WindowsAppSDK.Base 2.7.0 Microsoft.WindowsAppSDK.SelfContained.targets.
// Automation Forge adds this precompiled MSBuild wrapper so its executable
// assembly can be signed and verified before loading. See THIRD-PARTY-NOTICE.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Microsoft.Build.Framework;

public sealed class GenerateAppManifestFromAppxFragments : Microsoft.Build.Utilities.Task
{
    [Required] public bool RedirectDlls { get; set; }
    [Required] public string[] FrameworkDlls { get; set; }
    [Required] public string[] AppxFragments { get; set; }
    [Required] public string OutAppManifest { get; set; }

    public override bool Execute()
    {

            var headerF = @"<?xml version='1.0' encoding='utf-8' standalone='yes'?>
<assembly manifestVersion='1.0'
    xmlns:asmv3='urn:schemas-microsoft-com:asm.v3'
    xmlns:winrtv1='urn:schemas-microsoft-com:winrt.v1'
    xmlns='urn:schemas-microsoft-com:asm.v1'>";
            var sb = new StringBuilder();
            sb.AppendLine(headerF);
            
            var dllFileFormat = RedirectDlls ? 
                @"    <asmv3:file name='{0}' loadFrom='%MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%{0}'>" :
                @"    <asmv3:file name='{0}'>";
            var dllFiles = FrameworkDlls.Select(Path.GetFileName).ToList();

            foreach (String appxFragment in AppxFragments)
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(appxFragment);
                var nsmgr = new XmlNamespaceManager(doc.NameTable);
                nsmgr.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
                var xQuery = "./m:Fragment/m:Extensions/m:Extension/m:InProcessServer";
                foreach (XmlNode winRTFactory in doc.SelectNodes(xQuery, nsmgr))
                {
                    var dllFileNode = winRTFactory.SelectSingleNode("./m:Path", nsmgr);
                    var dllFile = dllFileNode.InnerText;
                    var typesNames = winRTFactory.SelectNodes("./m:ActivatableClass", nsmgr).OfType<XmlNode>();
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine();
                    foreach (var typeNode in typesNames)
                    {
                        var attribs = typeNode.Attributes.OfType<XmlAttribute>().ToArray();
                        var typeName = attribs
                            .OfType<XmlAttribute>()
                            .SingleOrDefault(x => x.Name == "ActivatableClassId")
                            .InnerText;
                        var xmlEntryFormat =
@"        <winrtv1:activatableClass name='{0}' threadingModel='both'/>";
                        sb.AppendFormat(xmlEntryFormat, typeName);
                        sb.AppendLine();
                        dllFiles.RemoveAll(e => e.Equals(dllFile, StringComparison.OrdinalIgnoreCase));
                    }
                    sb.AppendLine(@"    </asmv3:file>");
                }
				
                // Add ProxyStub elements to the generated appxmanifest
                xQuery = "./m:Fragment/m:Extensions/m:Extension/m:ProxyStub";
                foreach (XmlNode proxystub in doc.SelectNodes(xQuery, nsmgr))
                {
                    var classIDAdded = false;
                    
                    var dllFileNode = proxystub.SelectSingleNode("./m:Path", nsmgr);
                    var dllFile = dllFileNode.InnerText;
                    // exclude PushNotificationsLongRunningTask, which requires the Singleton (which is unavailable for self-contained apps)
                    // exclude Widgets entries unless/until they have been tested and verified by the Widgets team
                    if (dllFile == "PushNotificationsLongRunningTask.ProxyStub.dll" || dllFile == "Microsoft.Windows.Widgets.dll")
                    {
                        continue;
                    }
                    var typesNamesForProxy = proxystub.SelectNodes("./m:Interface", nsmgr).OfType<XmlNode>();
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine();
                    foreach (var typeNode in typesNamesForProxy)
                    {
                        if(!classIDAdded)
                        {
                            var classIdAttribute = proxystub.Attributes.OfType<XmlAttribute>().ToArray();
                            var classID = classIdAttribute
                                .OfType<XmlAttribute>()
                                .SingleOrDefault(x => x.Name == "ClassId")
                                .InnerText;

                            var xmlEntryFormat = @"        <asmv3:comClass clsid='{{{0}}}'/>";
                            sb.AppendFormat(xmlEntryFormat, classID);
                            classIDAdded = true;
                        }
                        var attribs = typeNode.Attributes.OfType<XmlAttribute>().ToArray();
                        var typeID = attribs
                            .OfType<XmlAttribute>()
                            .SingleOrDefault(x => x.Name == "InterfaceId")
                            .InnerText;
                        var typeNames = attribs
                            .OfType<XmlAttribute>()
                            .SingleOrDefault(x => x.Name == "Name")
                            .InnerText;
                        var xmlEntryFormatForStubs = @"        <asmv3:comInterfaceProxyStub name='{0}' iid='{{{1}}}'/>";
                        sb.AppendFormat(xmlEntryFormatForStubs, typeNames, typeID);
                        sb.AppendLine();
                        dllFiles.RemoveAll(e => e.Equals(dllFile, StringComparison.OrdinalIgnoreCase));
                    }
                    sb.AppendLine(@"    </asmv3:file>");
                }
            }
			
            if(RedirectDlls)
            {
                foreach (var dllFile in dllFiles)
                {
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine(@"</asmv3:file>");
                }
            }
			
            sb.AppendLine(@"</assembly>");
            var manifestContent = sb.ToString();
            File.WriteAllText(OutAppManifest, manifestContent, Encoding.UTF8);

        return true;
    }
}

