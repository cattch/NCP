using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Ncp
{
    static class DisplayRegressionTests
    {
        const string ClassKey=@"\Registry\Machine\System\CurrentControlSet\Control\Class\{4d36e96e-e325-11ce-bfc1-08002be10318}\";
        public static void Run(Action<bool,string> check,Action<Action,string> reject,string root)
        {
            var inactive=new MonitorIdentity {InterfaceId="OLD",Flags=0,RegistryKey=ClassKey+"0000"};
            var main=new MonitorIdentity {InterfaceId="MAIN",Flags=1,RegistryKey=ClassKey+"0007"};
            check(WindowsDisplayIdentity.FindActive(new[]{inactive,main})==main,"monitor selection skips a disconnected first child");
            check(WindowsDisplayIdentity.FindActive(new[]{inactive,main},"main")==main,"ICC identity matching is case insensitive and independent of display numbering");
            check(WindowsDisplayIdentity.FindActive(new[]{inactive,main},"OLD")==null,"a stale saved interface cannot redirect ICC changes to another monitor");
            reject(delegate {WindowsDisplayIdentity.FindActive(new[]{main,main});},"ambiguous active monitor bindings are rejected");
            string mainKey=ClassKey+"0007",subKey=ClassKey+"0002";
            string mainPath=IccUserProfileSetting.StoredPath(mainKey),subPath=IccUserProfileSetting.StoredPath(subKey);
            check(mainPath.EndsWith(@"\0007") && !string.Equals(mainPath,subPath),"per-user ICC flags address the exact monitor registry instance");
            check(IccUserProfileSetting.StoredPath(mainKey+@"\other")==null && IccUserProfileSetting.StoredPath(@"\Registry\Machine\Software\0007")==null,
                "ICC setting fallback cannot address unrelated registry paths");
            var access=new FlagAccess();access.Stored[mainPath]=false;access.Stored[subPath]=true;access.Ignored.Add(mainKey);
            var setting=new IccUserProfileSetting(access);setting.Set(mainKey,true);
            check(setting.Read(mainKey) && access.Stored[mainPath] && access.NativeWrites==1 && access.StoredWrites==1,
                "a successful Windows call that leaves the checkbox off is repaired and verified");
            check(access.Stored[subPath],"repairing the primary display never changes the secondary display flag");
            setting.Set(mainKey,true);
            check(access.NativeWrites==1 && access.StoredWrites==1,"an already enabled ICC scope is not unnecessarily reset");
            setting.Set(mainKey,false);setting.Set(mainKey,true);
            check(setting.Read(mainKey),"ICC scope can be restored and re-enabled after an ICC-free preset");
            access.Stored[mainPath]=false;access.NativeFailure=new Win32Exception(5);int writes=access.StoredWrites;
            reject(delegate {setting.Set(mainKey,true);},"real Windows access failures are not bypassed");
            check(access.StoredWrites==writes,"a denied native operation does not cause an unrequested fallback write");
            access.NativeFailure=null;access.IgnoreStored=true;
            reject(delegate {setting.Set(mainKey,true);},"an unsuccessful checkbox repair is never reported as applied");

            var api=new DualAssociations();var policy=new IccAssociationPolicy(api);
            var display=new DisplayTarget {Key="MAIN",GdiName=@"\\.\DISPLAY2",Primary=true};
            var secondary=new DisplayTarget {Key="SUB",GdiName=@"\\.\DISPLAY1"};
            string a="NCP-"+new string('a',64)+".icc",b="NCP-"+new string('b',64)+".icc";
            api.Main.Attached.AddRange(new[]{a,b,"factory.icm"});api.Main.Selected=b;
            var original=api.Read(display);
            policy.Apply(display,a,new[]{a,b});
            check(api.Read(display).PerUser && api.Read(display).ProfileName==a && !api.Main.Attached.Contains(b),
                "DISPLAY2 primary applies the requested ICC even when removing another ICC resets Windows' default");
            check(api.Read(secondary).ProfileName=="secondary.icm" && api.Sub.Attached.SequenceEqual(new[]{"secondary.icm"}),
                "primary ICC switching does not write the DISPLAY1 secondary");
            for(int i=0;i<4;i++) {policy.Restore(display,original,new[]{a,b});policy.Apply(display,a,new[]{a,b});}
            check(api.Read(display).ProfileName==a && api.Flags.Stored[IccUserProfileSetting.StoredPath(ClassKey+"0007")],
                "repeated ICC to no-ICC to ICC transitions keep working on the primary monitor");
            policy.Restore(display,original,new[]{a,b});
            check(!api.Read(display).PerUser && api.Read(display).ProfileName=="factory.icm" && !api.Main.Attached.Any(WindowsIccBackend.IsNcpProfile),
                "primary restoration restores its original scope and removes all managed associations");
            api.Main.Attached.Add(a);api.Main.Selected="factory.icm";api.IgnoreDefault=true;
            reject(delegate {policy.Apply(display,a,new[]{a,b});},"a genuinely wrong default selection remains an error");

            VerifySelection(check,reject,root);
        }
        static void VerifySelection(Action<bool,string> check,Action<Action,string> reject,string root)
        {
            var store=new Store(Path.Combine(root,"monitor-memory"));var colors=new TwoMonitors();
            using(var controller=new Controller(store,colors)) {
                controller.RefreshDisplays();
                check(new Store(store.DirectoryPath).Load().SelectedDisplay=="MAIN","the initial DISPLAY2 monitor choice is persisted");
                controller.Select(colors.Sub);controller.Select(colors.Main);
            }
            colors.Targets=new List<DisplayTarget>{colors.Sub,new DisplayTarget {Key="main",GdiName=@"\\.\DISPLAY9",Name="Main"}};
            using(var controller=new Controller(store,colors)) {
                controller.RefreshDisplays();
                check(controller.Selected.Key=="main" && controller.Selected.GdiName==@"\\.\DISPLAY9",
                    "restart remembers the selected monitor across enumeration order, primary flag and GDI number changes");
                colors.FailRead=true;
                reject(delegate {controller.Select(colors.Sub);},"monitor color-query errors remain visible");
                check(new Store(store.DirectoryPath).Load().SelectedDisplay=="SUB","a read failure does not discard the chosen monitor preference");
                colors.FailRead=false;
            }
            colors.Targets=new List<DisplayTarget>{colors.Main};
            using(var controller=new Controller(store,colors)) {
                controller.RefreshDisplays();
                check(controller.Selected.Key=="MAIN" && new Store(store.DirectoryPath).Load().SelectedDisplay=="SUB",
                    "temporary disconnection uses an available display without forgetting the preferred monitor");
                colors.Targets=new List<DisplayTarget>{colors.Main,colors.Sub};controller.RefreshDisplays();
                check(controller.Selected.Key=="SUB","reconnecting the remembered monitor restores its selection");
            }
            check(colors.Writes==0,"monitor selection and restart never apply display colors");
        }
        sealed class FlagAccess:IIccUserProfileAccess
        {
            internal Dictionary<string,bool> Stored=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
            internal Dictionary<string,bool> Native=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
            internal HashSet<string> Ignored=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal int NativeWrites,StoredWrites;internal Exception NativeFailure;internal bool IgnoreStored;
            public bool ReadNative(string key) {bool value;return Native.TryGetValue(key,out value)&&value;}
            public void WriteNative(string key,bool enabled) {
                NativeWrites++;if(NativeFailure!=null)throw NativeFailure;
                if(Ignored.Contains(key))return;
                Native[key]=enabled;Stored[IccUserProfileSetting.StoredPath(key)]=enabled;
            }
            public bool? ReadStored(string path) {bool value;return Stored.TryGetValue(path,out value)?(bool?)value:null;}
            public void WriteStored(string path,bool enabled) {StoredWrites++;if(!IgnoreStored)Stored[path]=enabled;}
        }
        sealed class MonitorState
        {
            internal string Key,Selected,Original;
            internal List<string> Attached=new List<string>();
        }
        sealed class DualAssociations:IIccAssociations
        {
            internal readonly FlagAccess Flags=new FlagAccess();readonly IccUserProfileSetting setting;
            internal MonitorState Main=new MonitorState {Key=ClassKey+"0007",Original="factory.icm"};
            internal MonitorState Sub=new MonitorState {Key=ClassKey+"0002",Original="secondary.icm",Selected="secondary.icm",Attached=new List<string>{"secondary.icm"}};
            internal bool IgnoreDefault;
            internal DualAssociations() {
                Flags.Stored[IccUserProfileSetting.StoredPath(Main.Key)]=false;
                Flags.Stored[IccUserProfileSetting.StoredPath(Sub.Key)]=true;Flags.Ignored.Add(Main.Key);
                setting=new IccUserProfileSetting(Flags);
            }
            MonitorState Get(DisplayTarget d) {return d.Key=="MAIN"?Main:Sub;}
            public IccState Read(DisplayTarget d) {var m=Get(d);bool enabled=setting.Read(m.Key);return new IccState {PerUser=enabled,ProfileName=enabled?m.Selected:m.Original,Profiles=m.Attached.ToList()};}
            public void UsePerUser(DisplayTarget d,bool enabled) {setting.Set(Get(d).Key,enabled);}
            public void Associate(DisplayTarget d,string name) {var m=Get(d);if(!m.Attached.Contains(name))m.Attached.Add(name);m.Selected=name;}
            public void SetDefault(DisplayTarget d,string name) {if(!IgnoreDefault)Get(d).Selected=name;}
            public void Remove(DisplayTarget d,string name) {var m=Get(d);m.Attached.Remove(name);m.Selected=m.Attached.LastOrDefault();}
        }
        sealed class TwoMonitors:IColorBackend
        {
            internal DisplayTarget Main=new DisplayTarget {Key="MAIN",GdiName=@"\\.\DISPLAY2",Name="Main",Primary=true};
            internal DisplayTarget Sub=new DisplayTarget {Key="SUB",GdiName=@"\\.\DISPLAY1",Name="Secondary"};
            internal List<DisplayTarget> Targets;internal bool FailRead;internal int Writes;
            internal TwoMonitors() {Targets=new List<DisplayTarget>{Main,Sub};}
            public List<DisplayTarget> Enumerate() {return Targets.ToList();}
            public DisplayReading Read(DisplayTarget d) {if(FailRead)throw new IOException("Read failure");return new DisplayReading {Vibrance=50,Hue=0};}
            public DisplayReading Apply(DisplayTarget d,ColorValues v,double[] c,ColorValues old,double[] oldChannels) {Writes++;return Read(d);}
            public void Dispose() { }
        }
    }
}
