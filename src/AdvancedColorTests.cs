using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ncp
{
    static class AdvancedColorTests
    {
        internal static void Run(Action<bool,string> check,Action<Action,string> reject)
        {
            check(WindowsDisplayPath.ValidLayout,"display configuration native layouts match the Windows ABI");
            var primary=new DisplayTarget {Key="MAIN",GdiName=@"\\.\DISPLAY2",Primary=true};
            var secondary=new DisplayTarget {Key="SUB",GdiName=@"\\.\DISPLAY1"};
            var mainPath=new DisplayPath {InterfaceId="main",GdiName=@"\\.\display2",Adapter=new AdapterLuid {Low=37},SourceId=3,TargetId=27};
            var subPath=new DisplayPath {InterfaceId="SUB",GdiName=@"\\.\DISPLAY1",Adapter=new AdapterLuid {Low=37},SourceId=0,TargetId=9};
            var paths=new[]{subPath,mainPath};
            check(WindowsDisplayPath.Match(paths,primary)==mainPath,"DISPLAY2 resolves by both GDI name and monitor interface, not enumeration index");
            reject(delegate {WindowsDisplayPath.Match(paths,new DisplayTarget {Key="SUB",GdiName=primary.GdiName});},
                "a display number cannot redirect an ICC operation to a different monitor interface");
            reject(delegate {WindowsDisplayPath.Match(new[]{mainPath,mainPath},primary);},"ambiguous display paths are rejected before ICC access");
            var native=new Profiles();var legacy=new VirtualizedLegacy(native);
            string a="NCP-"+new string('a',64)+".icc",b="NCP-"+new string('b',64)+".icc";
            var oldProfiles=new Profiles();var oldApi=new VirtualizedLegacy(oldProfiles) {AllowColorWrites=true};
            bool legacyMismatch=false;
            try {new IccAssociationPolicy(oldApi).Apply(primary,a,new[]{a,b});}
            catch(InvalidOperationException e) {legacyMismatch=e.Message.Contains("조회 ICC: 없음");}
            check(legacyMismatch && oldProfiles.Main.Selected==a,
                "legacy ACM virtualization reproduces the reported no-profile mismatch even after Windows has selected the requested ICC");
            var displayApi=new DisplayIccAssociations(legacy,native,d=>WindowsDisplayPath.Match(paths,d));
            var policy=new IccAssociationPolicy(displayApi);
            var original=displayApi.Read(primary);
            check(legacy.Read(primary).ProfileName==null && original.ProfileName=="factory.icm",
                "an ACM-virtualized legacy query can return no profile while explicit STANDARD readback preserves the actual original ICC");
            policy.Apply(primary,a,new[]{a,b});
            check(displayApi.Read(primary).ProfileName==a && legacy.Read(primary).ProfileName==null,
                "ACM's legacy no-profile response does not falsely fail a verified display-specific ICC application");
            policy.Apply(primary,b,new[]{a,b});
            check(displayApi.Read(primary).ProfileName==b && !native.Main.Names.Contains(a),
                "ACM ICC switching removes the previous managed association and selects the new STANDARD profile");
            policy.Restore(primary,original,new[]{a,b});
            check(displayApi.Read(primary).ProfileName=="factory.icm" && native.Main.Names.SequenceEqual(new[]{"factory.icm"}),
                "returning to the original under ACM removes NCP and preserves the actual original ICC");
            for(int i=0;i<3;i++) {policy.Apply(primary,a,new[]{a,b});policy.Restore(primary,original,new[]{a,b});}
            check(displayApi.Read(primary).ProfileName=="factory.icm" && !native.Main.Names.Any(WindowsIccBackend.IsNcpProfile),
                "repeated ACM ICC and no-ICC transitions leave no managed association behind");
            check(displayApi.Read(secondary).ProfileName=="secondary.icm" && native.Sub.Writes==0,
                "primary ACM operations preserve the other monitor's profile associations");
            check(native.StandardOnly && !native.AdvancedAssociation && native.Main.Hdr=="hdr-calibration.icc",
                "STANDARD ICC operations do not edit the HDR profile or HDR association list");
            check(legacy.ColorWrites==0 && legacy.ScopeWrites==0,"display ICC operations never mix in legacy ICC writes or toggle an already enabled scope");
            native.IgnoreDefault=true;native.Main.Names.Add(b);native.Main.Selected=null;
            reject(delegate {policy.Apply(primary,b,new[]{a,b});},"an associated ICC with no actual default still fails verification under ACM");
            native.IgnoreDefault=false;native.Failure=new UnauthorizedAccessException("Denied");
            reject(delegate {policy.Apply(primary,a,new[]{a,b});},"display API access errors remain visible instead of falling back to an unverifiable legacy success");
            check(legacy.ColorWrites==0,"a display API failure does not cause a second ICC write through the legacy API");
            native.Failure=null;native.Main.Scope=0;
            var systemOriginal=displayApi.Read(primary);policy.Apply(primary,a,new[]{a,b});policy.Restore(primary,systemOriginal,new[]{a,b});
            check(native.Main.Scope==0 && displayApi.Read(primary).ProfileName=="system.icm" && legacy.ScopeWrites==2,
                "ACM display associations can enable and restore the monitor's original system-wide scope");
        }
        sealed class State
        {
            internal int Scope=1,Writes;internal string Selected="factory.icm",Hdr="hdr-calibration.icc";
            internal List<string> Names=new List<string>{"factory.icm"};
        }
        sealed class Profiles:IDisplayIccApi
        {
            internal State Main=new State(),Sub=new State {Selected="secondary.icm",Names=new List<string>{"secondary.icm"}};
            internal bool StandardOnly=true,AdvancedAssociation,IgnoreDefault;internal Exception Failure;
            State Get(DisplayPath path) {
                if(path.Adapter.Low!=37 || (path.SourceId!=3 && path.SourceId!=0))throw new InvalidOperationException("Wrong adapter or source");
                return path.SourceId==3?Main:Sub;
            }
            void Fail() {if(Failure!=null)throw Failure;}
            public int Scope(DisplayPath path) {Fail();return Get(path).Scope;}
            public string DefaultName(DisplayPath path,int scope,int subtype) {
                Fail();StandardOnly&=subtype==7;
                if(subtype!=7)return null;return scope==0?"system.icm":Get(path).Selected;
            }
            public List<string> Names(DisplayPath path,int scope) {Fail();if(scope!=1)throw new InvalidOperationException("Wrong association scope");return Get(path).Names.ToList();}
            public void Associate(DisplayPath path,string name,bool setDefault,bool advanced) {
                Fail();var state=Get(path);state.Writes++;AdvancedAssociation|=advanced;
                if(!state.Names.Contains(name))state.Names.Add(name);if(setDefault)state.Selected=name;
            }
            public void SetDefault(DisplayPath path,string name,int subtype) {
                Fail();var state=Get(path);state.Writes++;StandardOnly&=subtype==7;
                if(!IgnoreDefault)state.Selected=name;
            }
            public void Remove(DisplayPath path,string name,bool advanced) {
                Fail();var state=Get(path);state.Writes++;AdvancedAssociation|=advanced;state.Names.Remove(name);state.Selected=state.Names.LastOrDefault();
            }
        }
        sealed class VirtualizedLegacy:IIccAssociations
        {
            readonly Profiles profiles;internal int ScopeWrites,ColorWrites;internal bool AllowColorWrites;
            internal VirtualizedLegacy(Profiles profiles) {this.profiles=profiles;}
            State Get(DisplayTarget display) {return display.Key=="MAIN"?profiles.Main:profiles.Sub;}
            public IccState Read(DisplayTarget display) {return new IccState {PerUser=Get(display).Scope==1,ProfileName=null,Profiles=Get(display).Names.ToList()};}
            public void UsePerUser(DisplayTarget display,bool enabled) {ScopeWrites++;Get(display).Scope=enabled?1:0;}
            DisplayPath PathFor(DisplayTarget display) {return new DisplayPath {Adapter=new AdapterLuid {Low=37},SourceId=display.Key=="MAIN"?3u:0u};}
            public void Associate(DisplayTarget display,string name) {ColorWrites++;if(!AllowColorWrites)throw new InvalidOperationException("Legacy ICC write");profiles.Associate(PathFor(display),name,false,false);}
            public void SetDefault(DisplayTarget display,string name) {ColorWrites++;if(!AllowColorWrites)throw new InvalidOperationException("Legacy ICC write");profiles.SetDefault(PathFor(display),name,7);}
            public void Remove(DisplayTarget display,string name) {ColorWrites++;if(!AllowColorWrites)throw new InvalidOperationException("Legacy ICC write");profiles.Remove(PathFor(display),name,false);}
        }
    }
}
