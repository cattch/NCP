using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.ComponentModel;

namespace Ncp
{
    static class FeatureTests
    {
        static void U32(byte[] data,int offset,uint value) {data[offset]=(byte)(value>>24);data[offset+1]=(byte)(value>>16);data[offset+2]=(byte)(value>>8);data[offset+3]=(byte)value;}
        static void Sign(byte[] data,int offset,string value) {Encoding.ASCII.GetBytes(value).CopyTo(data,offset);}
        static byte[] ProfileBytes()
        {
            var data=new byte[164];U32(data,0,(uint)data.Length);data[8]=4;Sign(data,12,"mntr");Sign(data,16,"RGB ");Sign(data,20,"XYZ ");Sign(data,36,"acsp");
            U32(data,128,1);Sign(data,132,"desc");U32(data,136,144);U32(data,140,20);Sign(data,144,"text");Sign(data,152,"test");return data;
        }
        public static void Run(Action<bool,string> check,Action<Action,string> reject,string directory)
        {
            string root=Path.Combine(directory,"icc-and-profiles");Directory.CreateDirectory(root);
            VerifyAssociations(check,reject,root);
            DisplayRegressionTests.Run(check,reject,root);
            AdvancedColorTests.Run(check,reject);
            VerifyBackupCleanup(check,reject,root);
            VerifyBundledProfile(check,reject,root);
            VerifyInitialProfileAddition(check,reject,root);
            VerifyDefaultPresetManagement(check,reject,root);
            var data=ProfileBytes();string original=Path.Combine(root,"야간.icc");File.WriteAllBytes(original,data);
            var library=new IccLibrary(Path.Combine(root,"program"));var reference=library.Import(original);
            check(File.Exists(library.PathFor(reference)) && library.PathFor(reference).StartsWith(library.DirectoryPath),"ICC import creates a program-local backup");
            File.Delete(original);check(library.Read(reference).SequenceEqual(data),"ICC backup survives deletion of the original file");
            var duplicate=library.Import("duplicate.icm",data);
            check(duplicate.Hash==reference.Hash && Directory.GetFiles(library.DirectoryPath).Length==1,"identical ICC imports share one backup");
            var invalid=(byte[])data.Clone();invalid[36]=0;reject(delegate {library.Import("bad.icc",invalid);},"reject invalid ICC signature");
            invalid=(byte[])data.Clone();Sign(invalid,16,"CMYK");reject(delegate {library.Import("printer.icc",invalid);},"reject non-RGB ICC");
            invalid=(byte[])data.Clone();U32(invalid,136,uint.MaxValue);reject(delegate {library.Import("broken.icc",invalid);},"reject out-of-bounds ICC tags");
            invalid=(byte[])data.Clone();Sign(invalid,132,"MHC2");reject(delegate {library.Import("advanced.icc",invalid);},"reject unsupported advanced-color ICC");
            reject(delegate {library.PathFor(new IccProfileRef {Hash="../../escape",Name="bad.icc"});},"ICC references cannot escape the backup directory");
            invalid=(byte[])data.Clone();invalid[163]=1;File.WriteAllBytes(library.PathFor(reference),invalid);
            reject(delegate {library.Read(reference);},"detect modified ICC backup");File.WriteAllBytes(library.PathFor(reference),data);

            var preset=new Preset {Id="shared",Name="야간 FPS",Values=new ColorValues {Gamma=1.15,Vibrance=75},Icc=reference};
            string single=Path.Combine(root,"야간 FPS.ncpreset");SharePackages.ExportPreset(single,preset,library);
            using(var zip=ZipFile.OpenRead(single))check(zip.Entries.Count==2 && zip.GetEntry("manifest.json")!=null,"single-preset package includes ICC in the same file");
            var receiving=new IccLibrary(Path.Combine(root,"receiving"));var restored=SharePackages.ImportPreset(single,receiving);
            check(restored.Values.Same(preset.Values) && restored.Icc.Hash==reference.Hash && receiving.Read(restored.Icc).SequenceEqual(data),"preset package round-trips values and ICC");
            string json=Path.Combine(root,"legacy-with-icc.json");Store.Export(json,preset,library);
            check(Store.Import(json,receiving).Icc.Hash==reference.Hash,"ICC JSON sharing remains importable");
            string corrupt=Path.Combine(root,"corrupt.ncpreset");
            using(var input=ZipFile.OpenRead(single))using(var output=ZipFile.Open(corrupt,ZipArchiveMode.Create)) {
                foreach(var entry in input.Entries)using(var source=entry.Open())using(var destination=output.CreateEntry(entry.FullName).Open()) {
                    if(entry.FullName.EndsWith(".icc")) {var changed=(byte[])data.Clone();changed[163]=42;destination.Write(changed,0,changed.Length);}
                    else source.CopyTo(destination);
                }
            }
            reject(delegate {SharePackages.ImportPreset(corrupt,receiving);},"reject corrupted packaged ICC");
            string traversal=Path.Combine(root,"traversal.ncpreset");
            using(var input=ZipFile.OpenRead(single))using(var output=ZipFile.Open(traversal,ZipArchiveMode.Create)) {
                foreach(var entry in input.Entries)using(var source=entry.Open())using(var destination=output.CreateEntry(entry.FullName).Open())source.CopyTo(destination);
                using(var extra=output.CreateEntry("../escape.txt").Open())extra.WriteByte(1);
            }
            reject(delegate {SharePackages.ImportPreset(traversal,receiving);},"reject unexpected package paths without extracting them");

            var color=new CountingColor();var windows=new CountingIcc();
            var store=new Store(Path.Combine(root,"collections"));
            string firstId,profileFile=Path.Combine(root,"게임.ncpprofile");
            using(var controller=new Controller(store,color,windows)) {
                controller.RefreshDisplays();firstId=controller.ActiveProfile.Id;
                var attached=controller.ImportIcc(library.PathFor(reference));
                var a=controller.SavePreset(null,"일반",new ColorValues());
                var b=controller.SavePreset(null,"야간",preset.Values,attached);
                controller.MovePreset(b.Id,0);
                check(controller.Data.Profiles.Count==1 && windows.Writes==0 && color.Writes==0,"old preset list becomes a profile without applying ICC or colors");
                var copy=controller.CreateProfile("게임",true);
                check(copy.Presets.Count==2 && copy.Presets.All(p=>p.Id!=a.Id&&p.Id!=b.Id) && copy.Order[0]==copy.Presets.Single(p=>p.Name=="야간").Id,"profile copy preserves order and ICC with independent preset IDs");
                var night=controller.Data.Presets.Single(p=>p.Name=="야간");
                controller.Apply(night);
                check(color.Writes==1 && windows.Writes==1 && controller.Data.IccOriginals.Count==1 && controller.HasReadableMatch,"manual preset application includes its ICC and captures the original Windows state");
                string originalFingerprint=controller.Data.IccOriginals[0].State.Fingerprint;
                controller.SwitchProfile(firstId);controller.SwitchProfile(copy.Id);
                check(windows.Writes==1 && color.Writes==1,"profile switching changes lists without applying anything");
                using(var reopened=new Controller(store,color,windows)) {
                    reopened.RefreshDisplays();check(reopened.ActiveProfile.Id==copy.Id && reopened.HasReadableMatch,"restart preserves active profile and ICC state without reapplying");
                }
                controller.Apply(controller.DefaultPreset());
                check(windows.Current.Fingerprint==originalFingerprint,"default preset restores the original Windows ICC");
                controller.Apply(night);int previousWrites=windows.Writes;
                color.Fail=true;
                var other=controller.SavePreset(null,"실패",new ColorValues {Vibrance=20},attached);
                reject(delegate {controller.Apply(other);},"surface NVIDIA failure during ICC application");
                check(windows.Writes==previousWrites+2 && windows.Current.ProfileName==Path.GetFileName(new IccLibrary(store.DirectoryPath).PathFor(attached)) && controller.LastApplied.PresetId==night.Id,"failed NVIDIA application restores the previous ICC and keeps the prior record");
                color.Fail=false;windows.Fail=true;int previousColors=color.Writes;
                reject(delegate {controller.Apply(other);},"surface ICC application failure");
                check(color.Writes==previousColors && controller.LastApplied.PresetId==night.Id,"failed ICC application never applies NVIDIA values");windows.Fail=false;
                windows.Current=new IccState {PerUser=true,ProfileName="external.icm"};controller.Refresh();
                check(!controller.HasReadableMatch,"external ICC changes clear the applied marker");
                controller.Apply(night);
                night=controller.SavePreset(night.Id,night.Name,night.Values,null);
                check(controller.LastAppliedPreset()==null,"removing ICC changes the saved preset identity");
                controller.Apply(night);check(windows.Current.Fingerprint==originalFingerprint,"ICC-free preset restores the original Windows profile");
                controller.SavePreset(other.Id,"실패",other.Values,attached);
                controller.ExportProfile(profileFile);
                using(var zip=ZipFile.OpenRead(profileFile))check(zip.Entries.Count==2,"profile package deduplicates shared ICC backups");
            }

            var receivingStore=new Store(Path.Combine(root,"other-program"));var otherColor=new CountingColor();var otherWindows=new CountingIcc();
            using(var controller=new Controller(receivingStore,otherColor,otherWindows)) {
                controller.RefreshDisplays();string originalProfileId=controller.ActiveProfile.Id;
                controller.SavePreset(null,"기존",new ColorValues());
                var imported=controller.ImportProfile(profileFile);
                check(controller.Data.Profiles.Count==2 && imported.Presets.Count==3 && controller.Data.Profiles.Single(p=>p.Id==originalProfileId).Presets.Single().Name=="기존","profile import adds a separate collection and preserves existing presets");
                check(controller.Presets().First().Name=="야간" && controller.Data.Presets.Single(p=>p.Icc!=null).Icc.Hash==reference.Hash,"profile import restores its order and bundled ICC");
                int count=controller.Data.Presets.Count;controller.Import(single);
                check(controller.Data.Presets.Count==count+1 && controller.Data.Profiles.Single(p=>p.Id==originalProfileId).Presets.Count==1,"individual preset import adds only to the selected profile");
                check(otherColor.Writes==0 && otherWindows.Writes==0,"preset and profile imports do not change Windows or NVIDIA settings");
                var another=controller.ImportProfile(profileFile);check(another.Name=="게임 (2)","duplicate imported profile names receive a suffix");
                controller.SwitchProfile(originalProfileId);check(controller.Data.Presets.Single().Name=="기존","switching back restores the original collection");
                using(var reopened=new Controller(receivingStore,otherColor,otherWindows))
                    check(reopened.ActiveProfile.Id==originalProfileId && reopened.Data.Profiles.Count==3,"profile collections persist across restart");
                controller.DeleteProfile();controller.DeleteProfile();
                reject(delegate {controller.DeleteProfile();},"last profile cannot be deleted");
                check(Directory.GetFiles(Path.Combine(receivingStore.DirectoryPath,"icc")).Length==1,"deleting profiles retains an ICC still used by the remaining profile");
            }
        }
        static void VerifyBundledProfile(Action<bool,string> check,Action<Action,string> reject,string directory)
        {
            var store=new Store(Path.Combine(directory,"bundled-first-run"),true);
            var color=new CountingColor();var windows=new CountingIcc();
            var library=new IccLibrary(store.DirectoryPath);
            string profileId;
            using(var controller=new Controller(store,color,windows)) {
                controller.RefreshDisplays();profileId=controller.ActiveProfile.Id;
                check(controller.CanSave && File.Exists(store.FilePath) && controller.Data.Profiles.Count==1 && controller.ActiveProfile.Name=="기본 프로필","first application startup persists the bundled default profile");
                check(controller.Presets().Select(p=>p.Name).SequenceEqual(new[]{"기본값","맑음","흐림 & 안개 & 비","야간","범용"}),"bundled presets preserve the supplied order and include one neutral default");
                var day=controller.Data.Presets.Single(p=>p.Name=="맑음");var fog=controller.Data.Presets.Single(p=>p.Name=="흐림 & 안개 & 비");var night=controller.Data.Presets.Single(p=>p.Name=="야간");var general=controller.Data.Presets.Single(p=>p.Name=="범용");
                check(day.Values.Same(new ColorValues {Contrast=40,Gamma=1.4,Vibrance=70}) && fog.Values.Same(new ColorValues {Brightness=45,Contrast=55,Gamma=1.25,Vibrance=80}) && night.Values.Same(new ColorValues {Brightness=30,Contrast=70,Gamma=1.75,Vibrance=85}) && general.Values.Same(new ColorValues {Gamma=0.95,Vibrance=70}),"bundled presets contain their configured NVIDIA color values");
                check(day.Icc.Hash==fog.Icc.Hash && general.Icc.Hash==fog.Icc.Hash && day.Icc.Name=="bright.icc" && night.Icc.Name=="more_brighter.icc" && day.Icc.Hash!=night.Icc.Hash,"bundled presets retain their supplied ICC associations");
                check(Directory.GetFiles(library.DirectoryPath).Length==2 && controller.Data.Presets.All(p=>IccLibrary.Hash(library.Read(p.Icc))==p.Icc.Hash),"first startup validates both ICC backups and stores the shared ICC only once beside settings");
                check(color.Writes==0 && windows.Writes==0 && controller.Data.Applied.Count==0 && controller.Data.IccOriginals.Count==0,"initial profile creation never applies ICC or NVIDIA colors and records no pretend application");
                controller.SavePreset(day.Id,"맑음 수정",new ColorValues {Gamma=1.2,Vibrance=65},day.Icc);
            }
            using(var reopened=new Controller(new Store(store.DirectoryPath,true),color,windows)) {
                check(reopened.ActiveProfile.Id==profileId && reopened.Data.Profiles.Count==1 && reopened.Data.Presets.Count==4 && reopened.Data.Presets.Any(p=>p.Name=="맑음 수정" && p.Values.Gamma==1.2),"restart preserves edits without duplicating or resetting bundled presets");
                foreach(var preset in reopened.Data.Presets.ToList())reopened.Delete(preset.Id);
            }
            using(var reopened=new Controller(new Store(store.DirectoryPath,true),color,windows)) {
                check(reopened.Presets().Count==1 && reopened.Data.Presets.Count==0 && Directory.GetFiles(library.DirectoryPath).Length==0,"deleted bundled presets and unused ICC backups stay deleted after restart");
                check(color.Writes==0 && windows.Writes==0,"restart and bundled-preset deletion never write display settings");
            }
            var existing=new Store(Path.Combine(directory,"bundled-existing"));
            existing.Save(new AppData {Presets=new List<Preset>{new Preset {Id="existing",Name="내 설정",Values=new ColorValues {Vibrance=72}}}});
            byte[] before=File.ReadAllBytes(existing.FilePath);
            using(var controller=new Controller(new Store(existing.DirectoryPath,true),color,windows))
                check(controller.Data.Presets.Count==1 && controller.Data.Presets[0].Name=="내 설정" && File.ReadAllBytes(existing.FilePath).SequenceEqual(before) && !Directory.Exists(Path.Combine(existing.DirectoryPath,"icc")),"existing installations retain their data without adding bundled presets or ICC files");
            var damaged=new Store(Path.Combine(directory,"bundled-damaged"),true);Directory.CreateDirectory(damaged.DirectoryPath);File.WriteAllText(damaged.FilePath,"broken");
            using(var controller=new Controller(damaged,color,windows))
                check(!controller.CanSave && File.ReadAllText(damaged.FilePath)=="broken" && !Directory.Exists(Path.Combine(damaged.DirectoryPath,"icc")),"damaged existing settings are preserved rather than replaced with bundled defaults");
            var backupOnly=new Store(Path.Combine(directory,"bundled-backup-only"),true);Directory.CreateDirectory(backupOnly.DirectoryPath);File.WriteAllText(backupOnly.FilePath+".bak","backup");
            reject(delegate {backupOnly.Load();},"an existing settings backup prevents treating a missing main settings file as a new installation");
            check(File.ReadAllText(backupOnly.FilePath+".bak")=="backup" && !File.Exists(backupOnly.FilePath),"first-run initialization preserves a stranded settings backup");
        }
        static void VerifyInitialProfileAddition(Action<bool,string> check,Action<Action,string> reject,string directory)
        {
            var store=new Store(Path.Combine(directory,"initial-profile-add"),true);
            var color=new CountingColor();var windows=new CountingIcc();var library=new IccLibrary(store.DirectoryPath);
            string addedId,originalId;
            using(var controller=new Controller(store,color,windows)) {
                controller.RefreshDisplays();originalId=controller.ActiveProfile.Id;
                var day=controller.Data.Presets.Single(p=>p.Name=="맑음");
                controller.Apply(day);var applied=controller.LastApplied;
                var previousIds=controller.Data.Presets.Select(p=>p.Id).ToList();
                foreach(var preset in controller.Data.Presets.ToList())controller.Delete(preset.Id);
                controller.SavePreset(null,"사용자 설정",new ColorValues {Gamma=1.4});
                check(Directory.GetFiles(library.DirectoryPath).Length==0,"initial profile can be added after the original presets and ICC backups were deleted");
                var added=controller.AddInitialProfile();addedId=added.Id;
                check(controller.Data.Profiles.Count==2 && added.Name=="기본 프로필 (2)" && controller.ActiveProfile.Id==added.Id,"adding the initial profile creates and selects a separate uniquely named profile");
                check(controller.Presets().Select(p=>p.Name).SequenceEqual(new[]{"기본값","맑음","흐림 & 안개 & 비","야간","범용"}) && added.Presets.Single(p=>p.Name=="맑음").Values.Same(new ColorValues {Contrast=40,Gamma=1.4,Vibrance=70}),"adding the initial profile restores the embedded presets and order");
                check(controller.Data.Profiles.Single(p=>p.Id==originalId).Presets.Single().Name=="사용자 설정" && added.Presets.All(p=>!previousIds.Contains(p.Id)),"initial profile addition preserves edited profiles and assigns fresh preset identities");
                check(Directory.GetFiles(library.DirectoryPath).Length==2 && added.Presets.All(p=>IccLibrary.Hash(library.Read(p.Icc))==p.Icc.Hash),"initial profile addition recreates validated local ICC backups");
                check(color.Writes==1 && windows.Writes==1 && windows.Installs==1 && object.ReferenceEquals(controller.LastApplied,applied) && controller.LastAppliedPreset()==null,"initial profile addition never applies or installs display profiles and preserves the actual applied record");
                var another=controller.AddInitialProfile();
                check(another.Name=="기본 프로필 (3)" && another.Presets.All(p=>added.Presets.All(q=>q.Id!=p.Id)) && Directory.GetFiles(library.DirectoryPath).Length==2,"repeated initial-profile additions create independent presets while sharing identical ICC backups");
                controller.SwitchProfile(added.Id);
            }
            using(var reopened=new Controller(new Store(store.DirectoryPath,true),color,windows)) {
                check(reopened.ActiveProfile.Id==addedId && reopened.Data.Profiles.Count==3 && reopened.Data.Profiles.Single(p=>p.Id==originalId).Presets.Single().Values.Gamma==1.4,"added initial profiles and existing custom data persist after restart");
                check(color.Writes==1 && windows.Writes==1 && windows.Installs==1,"restarting after initial-profile addition never changes display colors");
            }
            var broken=new Store(Path.Combine(directory,"initial-profile-blocked"));Directory.CreateDirectory(broken.DirectoryPath);File.WriteAllText(broken.FilePath,"broken");
            using(var controller=new Controller(broken,color,windows))
                reject(delegate {controller.AddInitialProfile();},"initial-profile addition is blocked when existing settings cannot be read safely");
            check(!Directory.Exists(Path.Combine(broken.DirectoryPath,"icc")),"blocked initial-profile addition does not create ICC backups");
        }
        static void VerifyDefaultPresetManagement(Action<bool,string> check,Action<Action,string> reject,string directory)
        {
            var store=new Store(Path.Combine(directory,"optional-default"));var color=new CountingColor();var windows=new CountingIcc();
            string originalId;string package=Path.Combine(directory,"no-default.ncpprofile");
            using(var controller=new Controller(store,color,windows)) {
                controller.RefreshDisplays();originalId=controller.ActiveProfile.Id;
                var custom=controller.SavePreset(null,"사용자 설정",new ColorValues {Gamma=1.4});
                var neutral=controller.DefaultPreset();controller.Apply(neutral);var applied=controller.LastApplied;
                controller.Delete("default");
                check(!controller.HasDefaultPreset && controller.Presets().Single().Id==custom.Id && controller.LastAppliedPreset()==null,
                    "deleting the applied default removes its row and applied badge without removing custom presets");
                check(color.Writes==1 && windows.Writes==0 && object.ReferenceEquals(controller.LastApplied,applied),
                    "default deletion preserves the actual display colors and independent application record");
                reject(delegate {controller.Apply(neutral);},"a deleted default cannot be applied through a stale row");
                controller.ExportProfile(package);var copied=controller.CreateProfile("복사",true);
                check(copied.DefaultPresetRemoved && controller.Presets().All(p=>p.Id!="default"),"profile copying preserves the removed default");
                controller.CreateProfile("빈 프로필",false);
                check(controller.HasDefaultPreset && controller.Presets().Single().Id=="default","a new profile starts with its own default preset");
                var imported=controller.ImportProfile(package);
                check(imported.DefaultPresetRemoved && controller.Presets().Single().Name=="사용자 설정",
                    "profile export and import preserve the absence of the default preset");
                controller.AddInitialProfile();check(controller.HasDefaultPreset && controller.Presets().Count==5,
                    "adding the initial profile includes its default independently of other profiles");
                controller.SwitchProfile(originalId);
                check(!controller.HasDefaultPreset,"switching profiles remembers each profile's default deletion separately");
                var restored=controller.AddDefaultPreset();
                check(controller.HasDefaultPreset && restored.Values.Same(new ColorValues()) && restored.Icc==null
                    && controller.Presets().Select(p=>p.Id).SequenceEqual(new[]{custom.Id,"default"}),
                    "default addition appends one neutral 50/50/1/50/0 preset without ICC and preserves custom ordering");
                var second=controller.AddDefaultPreset();var third=controller.AddDefaultPreset();
                check(second.Id!="default" && third.Id!=second.Id && second.Name=="기본값 (2)" && third.Name=="기본값 (3)"
                    && second.Values.Same(new ColorValues()) && third.Values.Same(new ColorValues()) && second.Icc==null && third.Icc==null,
                    "each additional default gets its own identity, numbered name and neutral values without ICC");
                check(controller.Presets().Count==4 && controller.LastAppliedPreset().Id=="default",
                    "adding duplicate defaults preserves the original default and its actual applied badge");
                using(var reopened=new Controller(new Store(store.DirectoryPath),color,windows))
                    check(reopened.Presets().Select(p=>p.Id).SequenceEqual(controller.Presets().Select(p=>p.Id)),
                        "duplicate defaults preserve independent identities and ordering across restart");
                controller.ExportProfile(package);
                var duplicateProfile=controller.ImportProfile(package);
                check(controller.Presets().Select(p=>p.Name).SequenceEqual(new[]{"사용자 설정","기본값","기본값 (2)","기본값 (3)"}),
                    "profile sharing retains every default copy in the correct order");
                controller.DeleteProfile();controller.SwitchProfile(originalId);
                controller.Delete(second.Id);
                check(controller.Presets().Any(p=>p.Id=="default") && controller.Presets().Any(p=>p.Id==third.Id),
                    "deleting one default copy preserves the other copies");
                controller.Delete(third.Id);
                check(color.Writes==1 && windows.Writes==0,"default addition, profile copying, importing and switching never apply display colors");
                controller.Delete("default");controller.Delete(custom.Id);
                check(controller.Presets().Count==0,"all presets including the default can be removed");
            }
            using(var reopened=new Controller(new Store(store.DirectoryPath),color,windows)) {
                check(reopened.ActiveProfile.Id==originalId && reopened.Presets().Count==0 && !reopened.HasDefaultPreset,
                    "an empty preset list and removed default persist after restarting");
                reopened.AddDefaultPreset();check(reopened.Presets().Single().Id=="default" && color.Writes==1 && windows.Writes==0,
                    "the default can be restored to an empty list without changing display colors");
            }
            VerifyDefaultPresetUi(check,directory);
        }
        static T UiField<T>(MainForm form,string field) where T:class
        {
            return (T)typeof(MainForm).GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(form);
        }
        static void Click(System.Windows.Forms.Button button)
        {
            button.PerformClick();
        }
        static void VerifyDefaultPresetUi(Action<bool,string> check,string directory)
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            var store=new Store(Path.Combine(directory,"optional-default-ui"));var color=new CountingColor();
            using(var controller=new Controller(store,color))using(var form=new MainForm(controller,null,false)) {
                form.ShowInTaskbar=false;form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;
                form.Location=new System.Drawing.Point(-32000,-32000);form.Show();System.Windows.Forms.Application.DoEvents();
                var grid=UiField<System.Windows.Forms.DataGridView>(form,"presets");
                var add=UiField<System.Windows.Forms.Button>(form,"defaultAdd");var delete=UiField<System.Windows.Forms.Button>(form,"delete");
                check(grid.Rows.Count==1 && delete.Enabled && add.Enabled,"the add button remains enabled while the original default is present");
                Click(add);Click(add);
                check(grid.Rows.Count==3 && add.Enabled && controller.Presets().Select(p=>p.Name).SequenceEqual(new[]{"기본값","기본값 (2)","기본값 (3)"})
                    && color.Writes==0,"two real add-button clicks create two separate defaults without applying display colors");
                Click(delete);Click(delete);Click(delete);Click(delete);
                Click(delete);Click(delete);
                check(grid.Rows.Count==0 && add.Enabled && !delete.Enabled && !UiField<System.Windows.Forms.Button>(form,"save").Enabled
                    && !UiField<System.Windows.Forms.Button>(form,"export").Enabled && UiField<System.Windows.Forms.Button>(form,"duplicate").Enabled,
                    "deleting the last default leaves an empty editor with working add and new-preset controls");
                Click(add);check(grid.Rows.Count==1 && add.Enabled && delete.Enabled && color.Writes==0,
                    "the actual add-button handler restores the default row without applying it");
                Click(delete);Click(delete);UiField<System.Windows.Forms.TextBox>(form,"name").Text="빈 목록에서 추가";
                Click(UiField<System.Windows.Forms.Button>(form,"duplicate"));
                check(grid.Rows.Count==1 && controller.Presets().Single().Name=="빈 목록에서 추가" && add.Enabled && color.Writes==0,
                    "a custom preset can be created from the empty editor after deleting the default");
                using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)) {form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(directory,"default-add-ui.png"));}
            }
        }
        static void VerifyBackupCleanup(Action<bool,string> check,Action<Action,string> reject,string directory)
        {
            string root=Path.Combine(directory,"cleanup");Directory.CreateDirectory(root);
            string firstFile=Path.Combine(root,"first.icc"),secondFile=Path.Combine(root,"second.icc");
            var bytes=ProfileBytes();File.WriteAllBytes(firstFile,bytes);bytes=(byte[])bytes.Clone();bytes[163]=1;File.WriteAllBytes(secondFile,bytes);
            var store=new Store(Path.Combine(root,"app"));var library=new IccLibrary(store.DirectoryPath);
            var color=new CountingColor();var windows=new CountingIcc();
            using(var controller=new Controller(store,color,windows)) {
                controller.RefreshDisplays();
                var first=controller.ImportIcc(firstFile);
                var a=controller.SavePreset(null,"A",new ColorValues(),first);var b=controller.SavePreset(null,"B",new ColorValues(),first);
                controller.Delete(a.Id);check(File.Exists(library.PathFor(first)),"deleting one preset keeps an ICC shared with another preset");
                controller.Delete(b.Id);check(!File.Exists(library.PathFor(first)),"deleting the last preset reference removes its ICC backup");
                first=controller.ImportIcc(firstFile);a=controller.SavePreset(null,"A",new ColorValues(),first);
                var copy=controller.CreateProfile("공유",true);controller.Delete(copy.Presets.Single().Id);
                check(File.Exists(library.PathFor(first)),"ICC reference counting includes inactive profiles");
                controller.DeleteProfile();
                check(File.Exists(library.PathFor(first)),"deleting a profile keeps an ICC referenced by a different profile");
                controller.CreateProfile("별도",false);var second=controller.ImportIcc(secondFile);
                var active=controller.SavePreset(null,"사용 중",new ColorValues(),second);controller.Apply(active);
                string fingerprint=windows.Current.Fingerprint;
                controller.DeleteProfile();
                check(!File.Exists(library.PathFor(second)) && File.Exists(library.PathFor(first)),"deleting a profile removes its exclusive ICC and preserves other profiles' backups");
                check(controller.LastApplied.Icc.Hash==second.Hash && windows.Current.Fingerprint==fingerprint && color.Writes==1 && windows.Writes==1,"deleting an applied profile frees its local backup without changing display settings");
                controller.Delete(a.Id);check(!File.Exists(library.PathFor(first)),"deleting the final cross-profile reference removes the shared ICC backup");
                first=controller.ImportIcc(firstFile);a=controller.SavePreset(null,"교체",new ColorValues(),first);
                second=controller.ImportIcc(secondFile);a=controller.SavePreset(a.Id,a.Name,a.Values,second);
                check(!File.Exists(library.PathFor(first)) && File.Exists(library.PathFor(second)),"replacing a preset ICC removes the unused old backup");
                controller.SavePreset(a.Id,a.Name,a.Values,null);
                check(!File.Exists(library.PathFor(second)),"clearing the final preset ICC removes its unused backup");
                first=controller.ImportIcc(firstFile);
                File.WriteAllText(Path.Combine(library.DirectoryPath,"manual.icc"),"keep");File.WriteAllText(Path.Combine(library.DirectoryPath,"NCP-invalid.icc"),"keep");
                controller.CleanupIccBackups();
                check(!File.Exists(library.PathFor(first)) && File.Exists(Path.Combine(library.DirectoryPath,"manual.icc")) && File.Exists(Path.Combine(library.DirectoryPath,"NCP-invalid.icc")),"orphan cleanup removes only NCP-managed backup filenames");
                check(File.Exists(firstFile) && File.Exists(secondFile) && color.Writes==1 && windows.Writes==1,"backup cleanup preserves imported source files and performs no display writes");
                first=controller.ImportIcc(firstFile);a=controller.SavePreset(null,"저장 보호",new ColorValues(),first);
                using(var locked=new FileStream(store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read))
                    reject(delegate {controller.Delete(a.Id);},"a failed preset-list save prevents backup cleanup");
                check(File.Exists(library.PathFor(first)) && new Store(store.DirectoryPath).Load().Presets.Any(p=>p.Id==a.Id),"failed deletion preserves the persisted preset and its ICC backup");
            }
            var corruptStore=new Store(Path.Combine(root,"bad"));Directory.CreateDirectory(corruptStore.DirectoryPath);File.WriteAllText(corruptStore.FilePath,"broken");
            var corruptLibrary=new IccLibrary(corruptStore.DirectoryPath);var kept=corruptLibrary.Import(firstFile);
            using(var controller=new Controller(corruptStore,new CountingColor(),new CountingIcc())) {
                controller.CleanupIccBackups();check(File.Exists(corruptLibrary.PathFor(kept)),"unreadable preset storage never causes ICC backups to be garbage-collected");
            }
        }
        static void VerifyAssociations(Action<bool,string> check,Action<Action,string> reject,string directory)
        {
            var display=new DisplayTarget {Key="test"};
            string a="NCP-"+new string('a',64)+".icc",b="NCP-"+new string('b',64)+".icc";
            var managed=new[]{a,b};var api=new AssociationApi();var policy=new IccAssociationPolicy(api);
            var implicitOriginal=api.Read(display);
            policy.Apply(display,a,managed);policy.Apply(display,b,managed);policy.Apply(display,a,managed);
            check(api.Attached.SequenceEqual(new[]{a}),"repeated ICC switching retains only the currently applied NCP association");
            policy.Restore(display,implicitOriginal,managed);
            check(api.Attached.Count==0 && api.Read(display).ProfileName==null,"removing NCP restores an implicit Windows default without setting a null ICC");
            api.Attached.Add("my-monitor.icm");api.Selected="my-monitor.icm";
            var customOriginal=api.Read(display);
            policy.Apply(display,a,managed);policy.Apply(display,b,managed);policy.Restore(display,customOriginal,managed);
            check(api.Attached.SequenceEqual(new[]{"my-monitor.icm"}) && api.Selected=="my-monitor.icm","restoration preserves and returns to an existing custom ICC");
            api.Attached.Add("other-user-profile.icc");api.Selected="other-user-profile.icc";
            var implicitLegacy=new IccState {PerUser=true};policy.Apply(display,a,managed);policy.Restore(display,implicitLegacy,managed);
            check(api.Selected=="other-user-profile.icc" && api.Attached.Count==2,"legacy empty-original records allow Windows to choose the pre-existing ICC");
            policy.Apply(display,a,managed);policy.Restore(display,customOriginal,managed);
            check(api.Selected=="other-user-profile.icc","normal restoration preserves a newer original ICC instead of forcing an old saved selection");
            api.PerUser=false;api.SystemProfile="factory.icm";var systemOriginal=api.Read(display);
            policy.Apply(display,a,managed);policy.Restore(display,systemOriginal,managed);
            check(!api.PerUser && api.Read(display).ProfileName=="factory.icm" && !api.Attached.Contains(a),"system-wide original is restored by removing NCP and disabling the user override");
            api.PerUser=true;api.Selected="my-monitor.icm";var before=api.Read(display);
            policy.Apply(display,a,managed);policy.Restore(display,before,managed,true);
            check(api.Selected=="my-monitor.icm" && !api.Attached.Contains(a),"rollback removes a new NCP attachment and restores the prior custom ICC");
            check(api.NullSelections==0,"ICC restoration never calls the failing null default-profile setter");
            var anomalous=new AssociationApi {AfterRemoveError=2015};var verified=new IccAssociationPolicy(anomalous);
            var empty=anomalous.Read(display);
            verified.Apply(display,a,managed);verified.Restore(display,empty,managed);
            check(anomalous.Attached.Count==0 && anomalous.Selected==null,"WCS false failure after successful disassociation is verified as restored");
            verified.Apply(display,a,managed);verified.Apply(display,b,managed);
            check(anomalous.Attached.SequenceEqual(new[]{b}) && anomalous.Selected==b,"switching ICC succeeds when WCS removes the old association but reports 2015");
            anomalous.AfterRemoveError=0;verified.Restore(display,empty,managed);
            check(anomalous.Attached.Count==0,"verified disassociation succeeds even when WCS returns an inconsistent zero error");
            int removeCount=anomalous.RemoveCount;verified.Restore(display,empty,managed);
            check(anomalous.RemoveCount==removeCount,"already removed associations are skipped on repeated restore");
            verified.Apply(display,a,managed);anomalous.AfterRemoveError=null;anomalous.BeforeRemoveError=5;
            reject(delegate {verified.Restore(display,empty,managed);},"a real disassociation failure remains an error when the ICC is still attached");
            check(anomalous.Selected==a && anomalous.Attached.Contains(a),"a failed disassociation does not pretend that the original ICC was restored");
            anomalous.BeforeRemoveError=null;anomalous.NoOpRemove=true;
            reject(delegate {verified.Restore(display,empty,managed);},"a success return without actually removing the ICC is rejected");
            var duplicates=new AssociationApi {Selected=b};
            duplicates.Attached.AddRange(new[]{"my-monitor.icm",a,b});
            var duplicatePolicy=new IccAssociationPolicy(duplicates);
            var contaminated=duplicates.Read(display);
            duplicatePolicy.Restore(display,contaminated,new[]{b});
            check(duplicates.Attached.SequenceEqual(new[]{"my-monitor.icm"}) && duplicates.Selected=="my-monitor.icm",
                "restoration removes both NCP profiles even when the old original snapshot and ownership list are incomplete");
            duplicates.Attached.AddRange(new[]{a,b});duplicates.Selected=b;
            duplicatePolicy.Apply(display,a,new string[0]);
            check(duplicates.Attached.SequenceEqual(new[]{"my-monitor.icm",a}) && duplicates.Selected==a,
                "applying a preset also removes an unrecorded second NCP association");
            duplicatePolicy.Restore(display,new IccState {PerUser=true,ProfileName=a,Profiles=new List<string>{a}},new[]{a,b},true);
            check(duplicates.Selected==a && duplicates.Attached.Contains(a),"failed application rollback can retain the exact prior NCP selection");
            duplicatePolicy.Restore(display,contaminated,new string[0]);
            check(!IccAssociationPolicy.ContainsNcp(duplicates.Read(display)),"normal restoration never protects NCP as an original ICC");
            var repairApi=new AssociationApi {Selected=b};repairApi.Attached.AddRange(new[]{"my-monitor.icm",a,b});
            var repairWindows=new AssociationBackend(repairApi);var repairColors=new CountingColor();
            var repairStore=new Store(Path.Combine(directory,"lost-icc-history"));
            using(var repair=new Controller(repairStore,repairColors,repairWindows)) {
                repair.RefreshDisplays();
                check(repair.Data.IccOriginals.Count==0 && repairWindows.Cleanups==0 && repairColors.Writes==0,
                    "startup leaves orphaned ICC profiles untouched until a manual click");
                repair.Apply(repair.DefaultPreset());
                check(repairApi.Attached.SequenceEqual(new[]{"my-monitor.icm"}) && repairApi.Selected=="my-monitor.icm" && repairWindows.Cleanups==1,
                    "default repairs both leftover profiles after reinstalling NCP without ICC history");
                check(repair.Data.IccOriginals[0].State.ProfileName==null && !IccAssociationPolicy.ContainsNcp(repair.Data.IccOriginals[0].State)
                    && !repairWindows.Protected.Any(WindowsIccBackend.IsNcpProfile),"new original records and cleanup protection exclude orphaned NCP profiles");
                repairApi.Attached.Add(a);repair.Refresh();
                check(!repair.HasReadableMatch && !repair.AppliedInSession,
                    "a leftover NCP attachment clears the default applied badge even when the selected original ICC has not changed");
                repair.Apply(repair.DefaultPreset());
                check(repair.HasReadableMatch && repairApi.Attached.SequenceEqual(new[]{"my-monitor.icm"}),
                    "the default button becomes usable again and repairs a later leftover association");
            }
            var staleStore=new Store(Path.Combine(directory,"stale-icc-original"));
            staleStore.Save(new AppData {IccOriginals=new List<IccOriginal>{new IccOriginal {DisplayKey="test",State=contaminated,ManagedProfiles=new List<string>{b}}}});
            repairApi.Attached.AddRange(new[]{a,b});repairApi.Selected=b;
            using(var repair=new Controller(staleStore,repairColors,repairWindows)) {
                repair.RefreshDisplays();repair.Apply(repair.DefaultPreset());
                check(repairApi.Attached.SequenceEqual(new[]{"my-monitor.icm"}) && !repairWindows.Protected.Any(WindowsIccBackend.IsNcpProfile),
                    "default repairs a legacy original record that mistakenly preserved a second NCP profile");
            }
            var cleanWindows=new AssociationBackend(new AssociationApi());
            using(var repair=new Controller(new Store(Path.Combine(directory,"orphan-install-only")),new CountingColor(),cleanWindows)) {
                repair.RefreshDisplays();repair.Apply(repair.DefaultPreset());
                check(cleanWindows.Cleanups==1,"default scans unused Windows installations even without device associations or ICC history");
            }
            var copyApi=new AssociationApi {Selected=a};copyApi.Attached.AddRange(new[]{"my-monitor.icm",a,b});
            using(var repair=new Controller(new Store(Path.Combine(directory,"copied-default-repair")),new CountingColor(),new AssociationBackend(copyApi))) {
                repair.RefreshDisplays();var copy=repair.AddDefaultPreset();repair.Apply(copy);
                check(copy.Id!="default" && copyApi.Attached.SequenceEqual(new[]{"my-monitor.icm"}) && repair.LastAppliedPreset().Id==copy.Id,
                    "applying a default copy performs the same orphaned NCP ICC cleanup as the original default");
                copyApi.Attached.Add(b);repair.Refresh();
                check(!repair.HasReadableMatch && !repair.AppliedInSession,"a leftover NCP ICC also invalidates the applied badge of a default copy");
            }
            string installed=Path.Combine(directory,"installed-fixture");Directory.CreateDirectory(installed);
            var bytes=ProfileBytes();string hash=IccLibrary.Hash(bytes);
            File.WriteAllBytes(Path.Combine(installed,"existing.icm"),bytes);File.WriteAllBytes(Path.Combine(installed,"NCP-"+hash+".icc"),bytes);
            check(WindowsIccBackend.FindInstalled(installed,hash)=="existing.icm","identical existing Windows ICC is reused before an NCP copy");
            check(WindowsIccBackend.FindInstalled(installed,new string('f',64))==null,"a different ICC is never mistaken for an installed match");
            string copyName="NCP-"+hash+".icc";
            check(WindowsIccBackend.UnusedCopies(installed,new[]{copyName}).Count==0,"an NCP installation used by another monitor remains protected");
            check(WindowsIccBackend.UnusedCopies(installed,new string[0]).Single()==Path.Combine(installed,copyName),"cleanup identifies an unused NCP installation without local backups or ownership history");
            var second=(byte[])bytes.Clone();second[163]=7;string secondName="NCP-"+IccLibrary.Hash(second)+".icc";
            File.WriteAllBytes(Path.Combine(installed,secondName),second);
            File.WriteAllBytes(Path.Combine(installed,"Custom gamma ICC profile.icc"),bytes);
            File.WriteAllBytes(Path.Combine(installed,"NCP-other.icc"),bytes);
            check(WindowsIccBackend.UnusedCopies(installed,new string[0]).Count==2,"cleanup selects both verified NCP copies and preserves unrelated similarly named files");
            check(WindowsIccBackend.UnusedCopies(installed,new[]{copyName}).Single()==Path.Combine(installed,secondName),"only unused copies are removed when another monitor still uses one");
            File.WriteAllBytes(Path.Combine(installed,copyName),new byte[164]);
            check(WindowsIccBackend.UnusedCopies(installed,new string[0]).SequenceEqual(new[]{Path.Combine(installed,secondName)}),"cleanup never deletes an unexpected or modified installed file");
        }
        sealed class AssociationBackend:IIccBackend
        {
            readonly AssociationApi api;readonly IccAssociationPolicy policy;
            public int Cleanups;public List<string> Protected=new List<string>();
            public AssociationBackend(AssociationApi api) {this.api=api;policy=new IccAssociationPolicy(api);}
            public IccState Read(DisplayTarget d) {return api.Read(d);}
            public string EnsureInstalled(string path,IccProfileRef p) {return Path.GetFileName(path);}
            public void Apply(DisplayTarget d,string name,IEnumerable<string> managed) {policy.Apply(d,name,managed);}
            public void Restore(DisplayTarget d,IccState state,IEnumerable<string> managed,bool exact=false) {policy.Restore(d,state,managed,exact);}
            public void CleanupUnused(IEnumerable<string> protectedNames) {Cleanups++;Protected=protectedNames.ToList();}
        }
        sealed class AssociationApi:IIccAssociations
        {
            public bool PerUser=true;public string Selected,SystemProfile;public int NullSelections;
            public int? BeforeRemoveError,AfterRemoveError;public bool NoOpRemove;public int RemoveCount;
            public List<string> Attached=new List<string>();
            public IccState Read(DisplayTarget d) {return new IccState {PerUser=PerUser,ProfileName=PerUser?Selected:SystemProfile,Profiles=new List<string>(Attached)};}
            public void UsePerUser(DisplayTarget d,bool enabled) {PerUser=enabled;}
            public void Associate(DisplayTarget d,string name) {if(!Attached.Contains(name))Attached.Add(name);Selected=name;}
            public void SetDefault(DisplayTarget d,string name) {if(name==null){NullSelections++;throw new IOException("No system-wide default is registered.");}Selected=name;}
            public void Remove(DisplayTarget d,string name) {
                RemoveCount++;
                if(BeforeRemoveError.HasValue)throw new Win32Exception(BeforeRemoveError.Value);
                if(!NoOpRemove) {Attached.Remove(name);if(Selected==name)Selected=Attached.LastOrDefault();}
                if(AfterRemoveError.HasValue)throw new Win32Exception(AfterRemoveError.Value);
            }
        }
        sealed class CountingColor:IColorBackend
        {
            public int Writes;public bool Fail;ColorValues current=new ColorValues();
            public List<DisplayTarget> Enumerate() {return new List<DisplayTarget>{new DisplayTarget {Key="test",Name="Test",GdiName="DISPLAY1"}};}
            public DisplayReading Read(DisplayTarget d) {return new DisplayReading {Vibrance=current.Vibrance,Hue=current.Hue};}
            public void Apply(DisplayTarget d,ColorValues v,double[] c,ColorValues rollback,double[] r) {Writes++;if(Fail)throw new InvalidOperationException("NVIDIA test failure");current=v.Copy();}
            public void Dispose() {}
        }
        sealed class CountingIcc:IIccBackend
        {
            public int Writes,Installs;public bool Fail;
            public IccState Current=new IccState {PerUser=false,ProfileName="original.icm"};
            public IccState Read(DisplayTarget d) {return new IccState {PerUser=Current.PerUser,ProfileName=Current.ProfileName};}
            public string EnsureInstalled(string path,IccProfileRef p) {Installs++;return Path.GetFileName(path);}
            public void Apply(DisplayTarget d,string name,IEnumerable<string> managed) {Writes++;Current=new IccState {PerUser=true,ProfileName=name};if(Fail)throw new InvalidOperationException("ICC test failure");}
            public void Restore(DisplayTarget d,IccState state,IEnumerable<string> managed,bool exact=false) {Writes++;Current=new IccState {PerUser=state.PerUser,ProfileName=state.ProfileName};}
            public void CleanupUnused(IEnumerable<string> protectedNames) {}
        }
    }
}
