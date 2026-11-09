using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ncp
{
    static class SelfTests
    {
        static int passed;
        static StringBuilder report;
        static void Check(bool condition,string message) {if(!condition)throw new Exception("FAIL: "+message);passed++;report.AppendLine("PASS: "+message);}
        static void Reject(Action action,string message){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,message);}
        public static int Run()
        {
            report=new StringBuilder();passed=0;int exit=0;
            string dir=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-artifacts",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            try {
                string executableDirectory=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                Check(Path.GetFullPath(Program.SettingsDirectory(false))==executableDirectory,"store presets beside the executable");
                Check(new Store(Program.SettingsDirectory(false)).FilePath==Path.Combine(executableDirectory,"settings.json"),"portable settings file location");
                string previousWorkingDirectory=Environment.CurrentDirectory;
                try {
                    Environment.CurrentDirectory=dir;
                    Check(Path.GetFullPath(Program.SettingsDirectory(false))==executableDirectory,"working directory does not change preset location");
                } finally {Environment.CurrentDirectory=previousWorkingDirectory;}
                Check(Program.SettingsDirectory(true)==Path.Combine(executableDirectory,"demo-data"),"demo settings stay separate inside the executable folder");
                Check(new Store(Path.Combine(dir,"fresh-install")).Load().Presets.Count==0,"fresh folder starts without imported presets or external fallback");
                VerifyPresetOrder(dir);
                FeatureTests.Run(Check,Reject,dir);
                Check(new ColorValues().Same(Preset.Default().Values),"neutral defaults");
                foreach(double v in new[]{double.NaN,double.PositiveInfinity,-1,101})Reject(delegate{new ColorValues {Brightness=v}.Validate();},"reject invalid brightness "+v);
                Reject(delegate{new ColorValues {Gamma=0}.Validate();},"reject zero gamma");
                Reject(delegate{new ColorValues {Hue=360}.Validate();},"reject hue 360");
                var neutral=NvidiaBackend.BuildRamp(new ColorValues(),null);
                Check(neutral.Length==3072 && neutral[0]==0 && neutral[3071]==1,"gamma ABI ramp length and endpoints");
                Check(Enumerable.Range(0,1024).All(i=>Math.Abs(neutral[i*3]-i/1023f)<.000001),"neutral ramp is identity");
                Check(Enumerable.Range(0,1024).All(i=>neutral[i*3]==neutral[i*3+1]&&neutral[i*3]==neutral[i*3+2]),"uniform RGB ramp");
                foreach(double b in new[]{0.0,50,100})foreach(double c in new[]{0.0,50,100})foreach(double g in new[]{.3,1,2.8}) {
                    var ramp=NvidiaBackend.BuildRamp(new ColorValues {Brightness=b,Contrast=c,Gamma=g},null);
                    Check(ramp.All(v=>!float.IsNaN(v)&&!float.IsInfinity(v)&&v>=0&&v<=1)&&Enumerable.Range(1,1023).All(i=>ramp[i*3]>=ramp[(i-1)*3]),"bounded monotonic LUT "+b+"/"+c+"/"+g);
                }
                var rgb=NvidiaBackend.BuildRamp(new ColorValues(),new double[]{50,50,50,50,50,50,1,1.2,1.4});
                Check(rgb[1500]<rgb[1501]&&rgb[1501]<rgb[1502],"preserve original per-channel gamma");
                Reject(delegate{NvidiaBackend.BuildRamp(new ColorValues(),new double[8]);},"reject malformed RGB snapshot");
                VerifyPanelGamma();
                var backend=new FakeBackend();var store=new Store(Path.Combine(dir,"profile"));
                var ctrl=new Controller(store,backend);ctrl.RefreshDisplays();
                Check(backend.Writes==0,"startup performs no display writes");
                Check(ctrl.Original!=null && ctrl.Original.Values.Gamma==1.1 && ctrl.Original.Values.Vibrance==75,"preserve the first panel snapshot independently of the default preset");
                Check(ctrl.DefaultPreset().Values.Same(new ColorValues()) && ctrl.DefaultPreset().Name=="기본값","default values stay at 50/50/1/50/0 despite a captured vibrance of 75 and non-neutral RGB gamma");
                ctrl.Delete("default");Check(!ctrl.HasDefaultPreset,"default can be deleted");ctrl.AddDefaultPreset();
                Reject(delegate{ctrl.SavePreset("default","overwrite",new ColorValues());},"default cannot be overwritten");
                var preset=ctrl.SavePreset(null,"경쟁 FPS",new ColorValues {Gamma=1.15,Vibrance=70});
                Check(backend.Writes==0,"saving a preset performs no display writes");
                string share=Path.Combine(dir,"shared.json");Store.Export(share,preset);
                var imported=ctrl.Import(share);Check(imported.Id!=preset.Id&&imported.Name=="경쟁 FPS (2)"&&imported.Values.Same(preset.Values),"export/import values and duplicate-name handling");
                Check(backend.Writes==0,"import performs no display writes");
                Reject(delegate{ctrl.SavePreset(null,"경쟁 FPS",new ColorValues());},"duplicate name rejected");
                ctrl.Refresh();Check(backend.Writes==0,"refresh performs no display writes");
                string oldFingerprint=ctrl.Reading.Fingerprint;
                ctrl.Apply(preset);Check(backend.Writes==1&&ctrl.AppliedInSession&&ctrl.LastApplied.PresetId==preset.Id,"only manual apply writes and records success");
                Check(ctrl.HasReadableMatch && ctrl.LastApplied.PanelFingerprint!=oldFingerprint,
                    "the applied marker records the new panel values instead of the previous fingerprint");
                ctrl.Refresh();Check(backend.Writes==1,"read-only refresh after apply");
                var reopened=new Controller(store,backend);reopened.RefreshDisplays();
                Check(backend.Writes==1&&!reopened.AppliedInSession&&reopened.StatusTitle().StartsWith("마지막 적용"),"reopen does not reapply or pretend full readback");
                Check(reopened.LastAppliedPreset()!=null&&reopened.LastAppliedPreset().Id==preset.Id&&reopened.HasReadableMatch,"reopen restores the last applied preset selection and badge");
                backend.Current.Hue=20;reopened.Refresh();Check(reopened.StatusTitle().StartsWith("외부 변경"),"detect external hue change");
                Check(!reopened.HasReadableMatch,"external changes remove the applied badge");
                ctrl.Delete(preset.Id);Check(backend.Writes==1&&ctrl.LastApplied.Values.Vibrance==70,"deleting applied preset preserves display and applied snapshot");
                Check(ctrl.LastAppliedPreset()==null,"deleted presets cannot remain selected as applied");
                backend.FailApply=true;Reject(delegate{ctrl.Apply(imported);},"surface backend write failure");
                Check(!ctrl.AppliedInSession&&ctrl.LastApplied.PresetId==preset.Id,"failed apply cannot mark new preset active");
                backend.FailApply=false;ctrl.Apply(ctrl.DefaultPreset());
                Check(backend.LastChannels==null && backend.Current.Same(new ColorValues()),"default application sends neutral values without replaying first-read RGB channels");
                Check(ctrl.LastAppliedPreset().Id=="default","default preset can be restored as the applied selection");
                Check(File.Exists(store.FilePath+".bak"),"atomic storage preserves prior backup");
                string bad=Path.Combine(dir,"bad.json");File.WriteAllText(bad,"{}");Reject(delegate{Store.Import(bad);},"missing required import fields rejected");
                File.WriteAllText(bad,"{\"Format\":\"NCP.Preset\",\"Version\":99,\"Name\":\"test\",\"Values\":{\"Brightness\":50,\"Contrast\":50,\"Gamma\":1,\"Vibrance\":50,\"Hue\":0}}");Reject(delegate{Store.Import(bad);},"future import version rejected");
                File.WriteAllText(bad,new string('x',65537));Reject(delegate{Store.Import(bad);},"oversized import rejected");
                string broken=Path.Combine(dir,"broken");Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,"settings.json"),"broken");
                var brokenCtrl=new Controller(new Store(broken),backend);brokenCtrl.RefreshDisplays();
                Check(!brokenCtrl.CanSave&&File.ReadAllText(Path.Combine(broken,"settings.json"))=="broken","corrupted storage preserved and mutation blocked");
                Reject(delegate{brokenCtrl.Apply(imported);},"apply blocked when original storage is unreadable");
                ctrl.Dispose();Check(backend.Writes==3,"exit performs no color restore or extra write");
            }catch(Exception e){exit=1;report.AppendLine(e.ToString());}
            report.AppendLine("RESULT: "+(exit==0?"PASS":"FAIL")+"; "+passed+" checks");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.txt"),report.ToString(),Encoding.UTF8);return exit;
        }
        static void VerifyPanelGamma()
        {
            var raw=NvidiaPanelGamma.Encode(new ColorValues {Brightness=45,Contrast=60,Gamma=1.25},null);
            Check(raw.SequenceEqual(new[]{98,98,98,104,104,104,125,125,125}),"panel persistence uses the driver's RGB value format");
            var saved=new Dictionary<string,int> {
                {NvidiaPanelGamma.Name(0),100},{NvidiaPanelGamma.RestoreFlag,0},{NvidiaPanelGamma.ReferenceMode,1},{"Unrelated",42}
            };
            var original=new Dictionary<string,int>(saved);
            Func<string,int?> read=name=>saved.ContainsKey(name)?(int?)saved[name]:null;
            Action<string,int?> write=(name,value)=> {
                if(value.HasValue)saved[name]=value.Value;
                else saved.Remove(name);
            };
            var update=new NvidiaPanelGamma(read,write,raw);
            update.Save();
            Check(update.Matches() && saved[NvidiaPanelGamma.RestoreFlag]==1 && saved["Unrelated"]==42
                && saved[NvidiaPanelGamma.ReferenceMode]==1,"saving panel gamma sets the restore flag and preserves other settings");
            Check(update.Restore() && saved.OrderBy(p=>p.Key).SequenceEqual(original.OrderBy(p=>p.Key)),
                "rolling back restores earlier DWORDs and removes values added by the failed apply");
            var partial=new NvidiaPanelGamma(read,(name,value)=> {
                if(name==NvidiaPanelGamma.RestoreFlag && value==1)throw new IOException("simulated write failure");
                write(name,value);
            },raw);
            Reject(partial.Save,"a write failure after the RGB values have been saved is reported");
            Check(partial.Restore() && saved.OrderBy(p=>p.Key).SequenceEqual(original.OrderBy(p=>p.Key)),
                "a partial write can be rolled back without leaving a saved curve behind");
            var concurrent=new NvidiaPanelGamma(read,write,raw);
            concurrent.Save();saved[NvidiaPanelGamma.Name(0)]=95;
            Check(!concurrent.Restore() && saved[NvidiaPanelGamma.Name(0)]==95 && saved.Count==original.Count,
                "rollback preserves an external change and restores the other saved values");
        }
        static void VerifyPresetOrder(string directory)
        {
            var backend=new FakeBackend();
            var store=new Store(Path.Combine(directory,"preset-order"));
            using(var controller=new Controller(store,backend)) {
                controller.RefreshDisplays();
                var a=controller.SavePreset(null,"A",new ColorValues {Vibrance=60});
                var b=controller.SavePreset(null,"B",new ColorValues {Vibrance=70});
                var c=controller.SavePreset(null,"C",new ColorValues {Vibrance=80});
                controller.Apply(b); int writes=backend.Writes;
                Check(controller.MovePreset(c.Id,1) && controller.Presets().Select(p=>p.Name).SequenceEqual(new[]{"기본값","C","A","B"}),"move a preset upward");
                Check(backend.Writes==writes && controller.LastAppliedPreset().Id==b.Id,"reordering preserves applied preset and never writes display colors");
                using(var reopened=new Controller(store,backend))
                    Check(reopened.Presets().Select(p=>p.Id).SequenceEqual(controller.Presets().Select(p=>p.Id)),"preset order persists across restart");
                controller.MovePreset(c.Id,4);
                Check(controller.Presets().Select(p=>p.Id).SequenceEqual(new[]{"default",a.Id,b.Id,c.Id}),"move a preset to the end");
                controller.MovePreset("default",4);
                Check(controller.Presets().Last().Id=="default","default preset can be reordered");
                controller.Delete("default");Check(!controller.HasDefaultPreset,"a reordered default preset can be deleted");controller.AddDefaultPreset();
                Check(!controller.MovePreset("default",4),"dropping in the same position is a no-op");
                controller.Delete(a.Id);
                Check(controller.Presets().Select(p=>p.Id).SequenceEqual(new[]{b.Id,c.Id,"default"}),"deleting preserves the remaining order");
                var d=controller.SavePreset(null,"D",new ColorValues());
                Check(controller.Presets().Last().Id==d.Id,"new presets append after the saved order");
                string shared=Path.Combine(directory,"order-import.json"); Store.Export(shared,d);
                var imported=controller.Import(shared);
                Check(controller.Presets().Last().Id==imported.Id && backend.Writes==writes,"imported presets append without applying colors");
                Reject(delegate {controller.MovePreset("missing",0);},"reject unknown preset in reorder request");
                Reject(delegate {controller.MovePreset(b.Id,99);},"reject invalid reorder position");
            }
            string legacy=Path.Combine(directory,"legacy-order");Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy,"settings.json"),"{\"Version\":1,\"Presets\":[]}");
            using(var controller=new Controller(new Store(legacy),backend))
                Check(controller.CanSave && controller.Presets().Single().Id=="default","load older settings without an order field");
            string blocked=Path.Combine(directory,"blocked-order");File.WriteAllText(blocked,"not a directory");
            using(var controller=new Controller(new Store(blocked),backend)) {
                controller.Data.Presets.Add(new Preset {Id="test",Name="Test",Values=new ColorValues()});
                Reject(delegate {controller.MovePreset("test",0);},"surface failed order persistence");
                Check(controller.Presets().First().Id=="default","failed persistence restores the original order");
            }
        }
        sealed class FakeBackend:IColorBackend
        {
            public int Writes;public bool FailApply;public ColorValues Current=new ColorValues {Vibrance=75};public double[] LastChannels;
            public List<DisplayTarget> Enumerate(){return new List<DisplayTarget>{new DisplayTarget {Key="test-monitor",Name="Test",GdiName="DISPLAY1"}};}
            public DisplayReading Read(DisplayTarget d){return new DisplayReading {Vibrance=Current.Vibrance,Hue=Current.Hue,Fingerprint=Current.Gamma.ToString("R",System.Globalization.CultureInfo.InvariantCulture),
                PanelValues=new ColorValues {Gamma=1.1,Vibrance=75},Channels=new double[]{50,50,50,50,50,50,1.1,1.2,1.3}};}
            public DisplayReading Apply(DisplayTarget d,ColorValues v,double[] channels,ColorValues rollback,double[] rc){Writes++;if(FailApply)throw new InvalidOperationException("test failure");Current=v.Copy();LastChannels=channels;return Read(d);}
            public void Dispose(){}
        }
    }
}
