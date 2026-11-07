using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using Microsoft.Win32;

namespace Ncp
{
    public interface IIccBackend
    {
        IccState Read(DisplayTarget display);
        string EnsureInstalled(string profilePath,IccProfileRef profile);
        void Apply(DisplayTarget display,string profileName,IEnumerable<string> managed);
        void Restore(DisplayTarget display,IccState state,IEnumerable<string> managed,bool exact=false);
        void CleanupUnused(IEnumerable<string> protectedNames);
    }
    public sealed class WindowsIccBackend:IIccBackend
    {
        const uint MonitorClass=0x6d6e7472;
        const int CurrentUser=1,SystemWide=0,IccType=0,NoSubtype=4;
        readonly IIccAssociations associations;
        public WindowsIccBackend() {associations=new NativeAssociations();}
        internal WindowsIccBackend(IIccAssociations associations) {this.associations=associations;}
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsGetUsePerUserProfiles(string device,uint deviceClass,out bool perUser);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsSetUsePerUserProfiles(string device,uint deviceClass,bool perUser);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsGetDefaultColorProfileSize(int scope,string device,int type,int subtype,uint id,out uint size);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsGetDefaultColorProfile(int scope,string device,int type,int subtype,uint id,uint size,StringBuilder name);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsSetDefaultColorProfile(int scope,string device,int type,int subtype,uint id,string name);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsAssociateColorProfileWithDevice(int scope,string profile,string device);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsDisassociateColorProfileFromDevice(int scope,string profile,string device);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsEnumColorProfilesSize(int scope,ref EnumRecord record,out uint size);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WcsEnumColorProfiles(int scope,ref EnumRecord record,[Out] byte[] buffer,uint size,out uint count);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool InstallColorProfileW(string machine,string path);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool UninstallColorProfileW(string machine,string path,bool delete);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetColorDirectoryW(string machine,StringBuilder directory,ref uint size);
        [DllImport("mscms.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenColorProfileW(ref NativeProfile profile,uint access,uint share,uint creation);
        [DllImport("mscms.dll",SetLastError=true)] static extern bool IsColorProfileValid(IntPtr profile,out bool valid);
        [DllImport("mscms.dll")] static extern bool CloseColorProfile(IntPtr profile);
        [StructLayout(LayoutKind.Sequential)] struct NativeProfile {public uint Type;public IntPtr Data;public uint Size;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct EnumRecord {
            public uint Size,Version,Fields;[MarshalAs(UnmanagedType.LPWStr)]public string DeviceName;
            public uint Media,Dither,ResolutionX,ResolutionY,Cmm,Class,Data,Connection,Signature,Platform,Flags,Manufacturer,Model,Attributes0,Attributes1,Intent,Creator,DeviceClass;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Device {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Description;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Id;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Key;
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string name,uint index,ref Device device,uint flags);
        static void Check(bool success,string action)
        {
            if(!success) {
                int error=Marshal.GetLastWin32Error();
                throw new Win32Exception(error,action+"에 실패했습니다. (Windows 오류 "+error+": "+new Win32Exception(error).Message+")");
            }
        }
        static string DeviceKey(DisplayTarget display)
        {
            return WindowsDisplayIdentity.ResolveKey(display);
        }
        static string DefaultName(string key,int scope)
        {
            uint size;string name=null;
            if(!WcsGetDefaultColorProfileSize(scope,key,IccType,NoSubtype,0,out size)) {
                int error=Marshal.GetLastWin32Error();
                if(error!=2 && error!=1168)throw new Win32Exception(error,"Windows 기본 ICC 조회에 실패했습니다.");
            } else {
                if(size==0 || size>65536)throw new InvalidDataException("Windows ICC 이름 길이가 올바르지 않습니다.");
                var buffer=new StringBuilder((int)size/2+1);
                Check(WcsGetDefaultColorProfile(scope,key,IccType,NoSubtype,0,size,buffer),"Windows 기본 ICC 조회");
                name=string.IsNullOrWhiteSpace(buffer.ToString())?null:Path.GetFileName(buffer.ToString());
            }
            return name;
        }
        static List<string> ProfileNames(string key,int scope)
        {
            var record=new EnumRecord {Size=(uint)Marshal.SizeOf(typeof(EnumRecord)),Version=0x300,Fields=1,DeviceName=key};
            uint size;Check(WcsEnumColorProfilesSize(scope,ref record,out size),"ICC 연결 목록 조회");
            if(size==0)return new List<string>();
            if(size>1024*1024 || size%2!=0)throw new InvalidDataException("Windows ICC 연결 목록 크기가 올바르지 않습니다.");
            var bytes=new byte[(int)size];uint count;
            Check(WcsEnumColorProfiles(scope,ref record,bytes,size,out count),"ICC 연결 목록 조회");
            if(count>2048)throw new InvalidDataException("Windows ICC 연결 목록이 너무 깁니다.");
            return Encoding.Unicode.GetString(bytes).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        sealed class NativeAssociations:IIccAssociations
        {
            readonly IccUserProfileSetting userProfiles=new IccUserProfileSetting(new NativeUserProfileAccess());
            public IccState Read(DisplayTarget display) {
                string key=DeviceKey(display);bool perUser=userProfiles.Read(key);
                var state=new IccState {PerUser=perUser,ProfileName=DefaultName(key,perUser?CurrentUser:SystemWide),Profiles=ProfileNames(key,CurrentUser)};
                state.Validate();return state;
            }
            public void UsePerUser(DisplayTarget display,bool enabled) {userProfiles.Set(DeviceKey(display),enabled);}
            public void Associate(DisplayTarget display,string name) {Check(WcsAssociateColorProfileWithDevice(CurrentUser,name,DeviceKey(display)),"ICC 모니터 연결");}
            public void SetDefault(DisplayTarget display,string name) {Check(WcsSetDefaultColorProfile(CurrentUser,DeviceKey(display),IccType,NoSubtype,0,name),"기본 ICC 적용");}
            public void Remove(DisplayTarget display,string name) {Check(WcsDisassociateColorProfileFromDevice(CurrentUser,name,DeviceKey(display)),"이전 NCP ICC 연결 해제");}
        }
        sealed class NativeUserProfileAccess:IIccUserProfileAccess
        {
            public bool ReadNative(string key) {bool enabled;Check(WcsGetUsePerUserProfiles(key,MonitorClass,out enabled),"Windows ICC 설정 조회");return enabled;}
            public void WriteNative(string key,bool enabled) {Check(WcsSetUsePerUserProfiles(key,MonitorClass,enabled),"사용자 ICC 설정 변경");}
            public bool? ReadStored(string path) {
                using(var key=Registry.CurrentUser.OpenSubKey(path)) {
                    if(key==null)return null;
                    object value=key.GetValue("UsePerUserProfiles",null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if(value==null)return null;
                    if(!(value is int) || ((int)value!=0 && (int)value!=1))throw new InvalidDataException("사용자별 ICC 설정 값이 올바르지 않습니다.");
                    return (int)value==1;
                }
            }
            public void WriteStored(string path,bool enabled) {
                using(var key=Registry.CurrentUser.CreateSubKey(path))key.SetValue("UsePerUserProfiles",enabled?1:0,RegistryValueKind.DWord);
            }
        }
        public IccState Read(DisplayTarget display) {return associations.Read(display);}
        internal static string DescribeTarget(DisplayTarget display)
        {
            string key=DeviceKey(display);var native=new NativeUserProfileAccess();
            string path=IccUserProfileSetting.StoredPath(key);bool? stored=path==null?null:native.ReadStored(path);
            return "ICC device key: "+key+"\nPer-user API: "+native.ReadNative(key)+
                "\nPer-user stored: "+(stored.HasValue?stored.Value.ToString():"not set");
        }
        public static bool IsNcpProfile(string name)
        {
            return IccLibrary.IsBackupName(name);
        }
        public static string ColorDirectory()
        {
            uint size=65536;var directory=new StringBuilder(32768);
            Check(GetColorDirectoryW(null,directory,ref size),"Windows 색상 프로필 폴더 조회");return directory.ToString();
        }
        static void ValidateNative(string path)
        {
            IntPtr name=Marshal.StringToHGlobalUni(path),handle=IntPtr.Zero;
            try {
                var profile=new NativeProfile {Type=1,Data=name,Size=(uint)((path.Length+1)*2)};
                handle=OpenColorProfileW(ref profile,1,1,3);
                if(handle==IntPtr.Zero)throw new InvalidDataException("Windows에서 이 ICC 파일을 열 수 없습니다.");
                bool valid;Check(IsColorProfileValid(handle,out valid),"ICC 유효성 확인");
                if(!valid)throw new InvalidDataException("Windows에서 유효한 ICC로 인식되지 않는 파일입니다.");
            } finally {if(handle!=IntPtr.Zero)CloseColorProfile(handle);Marshal.FreeHGlobal(name);}
        }
        public string EnsureInstalled(string profilePath,IccProfileRef profile)
        {
            using(var locked=new FileStream(profilePath,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                using(var memory=new MemoryStream()) {
                    locked.CopyTo(memory);var bytes=memory.ToArray();IccLibrary.ValidateBytes(bytes);
                    if(IccLibrary.Hash(bytes)!=profile.Hash)throw new InvalidDataException("ICC 백업 파일이 변경되었습니다.");
                }
                string existing=FindInstalled(profile.Hash);
                if(existing!=null)return existing;
                EnsureInstalledLocked(profilePath,profile);return Path.GetFileName(profilePath);
            }
        }
        internal static string FindInstalled(string directory,string hash)
        {
            // Prefer the user's existing installation over a former NCP copy.
            foreach(string path in Directory.GetFiles(directory).OrderBy(p=>IsNcpProfile(Path.GetFileName(p))?1:0)) {
                string extension=Path.GetExtension(path);
                if(!extension.Equals(".icc",StringComparison.OrdinalIgnoreCase) && !extension.Equals(".icm",StringComparison.OrdinalIgnoreCase))continue;
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)continue;
                try {if(IccLibrary.Hash(IccLibrary.ReadBytes(path))==hash)return Path.GetFileName(path);}
                catch(InvalidDataException) { }
                catch(IOException) { }
            }
            return null;
        }
        static string FindInstalled(string hash) {return FindInstalled(ColorDirectory(),hash);}
        void EnsureInstalledLocked(string profilePath,IccProfileRef profile)
        {
            ValidateNative(profilePath);
            string installed=Path.Combine(ColorDirectory(),Path.GetFileName(profilePath));
            if(File.Exists(installed)) {
                if(IccLibrary.Hash(IccLibrary.ReadBytes(installed))!=profile.Hash)throw new InvalidDataException("Windows에 같은 이름의 다른 ICC가 있습니다.");
                return;
            }
            if(InstallColorProfileW(null,profilePath))return;
            int error=Marshal.GetLastWin32Error();
            if(error!=5 && error!=1314)throw new Win32Exception(error,"ICC 설치에 실패했습니다.");
            var start=new ProcessStartInfo {
                FileName=typeof(WindowsIccBackend).Assembly.Location,
                Arguments="--install-icc "+profile.Hash,UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden
            };
            try {
                using(var process=Process.Start(start)) {
                    process.WaitForExit();
                    if(process.ExitCode!=0)throw new InvalidOperationException("ICC 설치를 완료하지 못했습니다. (오류 "+process.ExitCode+")");
                }
            } catch(Win32Exception e) {
                if(e.NativeErrorCode==1223)throw new InvalidOperationException("ICC 설치 권한 요청을 취소했습니다.",e);
                throw;
            }
            if(!File.Exists(installed) || IccLibrary.Hash(IccLibrary.ReadBytes(installed))!=profile.Hash)
                throw new InvalidOperationException("설치된 ICC 파일을 확인할 수 없습니다.");
        }
        public static int InstallFromCommandLine(string hash)
        {
            try {
                var profile=new IccProfileRef {Hash=hash,Name="install.icc"};profile.Validate();
                var library=new IccLibrary(AppDomain.CurrentDomain.BaseDirectory);library.Read(profile);
                string path=library.PathFor(profile);
                // Keep the validated file locked against changes while Windows copies it.
                using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    using(var memory=new MemoryStream()) {file.CopyTo(memory);if(IccLibrary.Hash(memory.ToArray())!=hash)return 2;}
                    ValidateNative(path);
                    return InstallColorProfileW(null,path)?0:Math.Max(1,Marshal.GetLastWin32Error());
                }
            } catch {return 2;}
        }
        public void Apply(DisplayTarget display,string profileName,IEnumerable<string> managed)
        {
            new IccAssociationPolicy(associations).Apply(display,profileName,managed);
        }
        public void Restore(DisplayTarget display,IccState state,IEnumerable<string> managed,bool exact=false)
        {
            new IccAssociationPolicy(associations).Restore(display,state,managed,exact);
        }
        static IEnumerable<string> MonitorKeys()
        {
            var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(uint a=0;a<64;a++) {
                var adapter=new Device {Size=Marshal.SizeOf(typeof(Device))};
                if(!EnumDisplayDevices(null,a,ref adapter,0))break;
                for(uint m=0;m<32;m++) {
                    var monitor=new Device {Size=Marshal.SizeOf(typeof(Device))};
                    if(!EnumDisplayDevices(adapter.Name,m,ref monitor,0))break;
                    if(!string.IsNullOrWhiteSpace(monitor.Key))keys.Add(monitor.Key);
                }
            }
            return keys;
        }
        static List<string> UnusedCopies(IEnumerable<string> protectedNames)
        {
            var keep=new HashSet<string>(protectedNames.Where(n=>n!=null),StringComparer.OrdinalIgnoreCase);
            var candidates=UnusedCopies(ColorDirectory(),keep);
            if(candidates.Count==0)return candidates;
            foreach(string key in MonitorKeys())foreach(int scope in new[]{SystemWide,CurrentUser}) {
                keep.UnionWith(ProfileNames(key,scope));
                string name=DefaultName(key,scope);if(name!=null)keep.Add(name);
            }
            // Protect global RGB working profiles too, even if they are not monitor associations.
            foreach(int scope in new[]{SystemWide,CurrentUser}) {
                uint size;
                bool found=WcsGetDefaultColorProfileSize(scope,null,IccType,5,0,out size);
                if(!found) {
                    int error=Marshal.GetLastWin32Error();
                    if(error!=2 && error!=1168)throw new Win32Exception(error,"기본 RGB ICC 조회에 실패했습니다.");
                } else if(size>0 && size<=65536) {
                    var name=new StringBuilder((int)size/2+1);
                    Check(WcsGetDefaultColorProfile(scope,null,IccType,5,0,size,name),"기본 RGB ICC 조회");keep.Add(Path.GetFileName(name.ToString()));
                }
            }
            if(candidates.All(p=>keep.Contains(Path.GetFileName(p))))return new List<string>();
            // Retain copies referenced by other loaded Windows accounts, including
            // disconnected monitors recorded in their association settings.
            foreach(string user in Registry.Users.GetSubKeyNames())
                using(var root=Registry.Users.OpenSubKey(user+@"\Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations"))
                    AddReferencedNames(root,keep,0);
            using(var monitors=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e96e-e325-11ce-bfc1-08002be10318}"))
                if(monitors!=null)foreach(string child in monitors.GetSubKeyNames().Where(n=>n.Length==4 && n.All(char.IsDigit)))
                    using(var device=monitors.OpenSubKey(child))AddReferencedValues(device,keep);
            return candidates.Where(p=>!keep.Contains(Path.GetFileName(p))).ToList();
        }
        static void AddReferencedNames(RegistryKey key,HashSet<string> keep,int depth)
        {
            if(key==null)return;
            if(depth>16)throw new InvalidDataException("ICC 연결 정보의 깊이가 올바르지 않습니다.");
            AddReferencedValues(key,keep);
            foreach(string child in key.GetSubKeyNames())using(var sub=key.OpenSubKey(child))AddReferencedNames(sub,keep,depth+1);
        }
        static void AddReferencedValues(RegistryKey key,HashSet<string> keep)
        {
            if(key==null)return;
            foreach(string valueName in key.GetValueNames()) {
                object value=key.GetValue(valueName,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                var names=value as string[];if(names==null && value is string)names=new[]{(string)value};
                if(names!=null)foreach(string name in names)if(!string.IsNullOrWhiteSpace(name)) {
                    try {string extension=Path.GetExtension(name);if(extension.Equals(".icc",StringComparison.OrdinalIgnoreCase) || extension.Equals(".icm",StringComparison.OrdinalIgnoreCase))keep.Add(Path.GetFileName(name));}
                    catch(ArgumentException) { }
                }
            }
        }
        internal static List<string> UnusedCopies(string installedDirectory,IEnumerable<string> protectedNames)
        {
            var keep=new HashSet<string>(protectedNames.Where(n=>n!=null),StringComparer.OrdinalIgnoreCase);var result=new List<string>();
            foreach(string path in Directory.GetFiles(installedDirectory,"NCP-*.icc")) {
                string name=Path.GetFileName(path);
                if(!IsNcpProfile(name) || keep.Contains(name) || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)continue;
                var profile=new IccProfileRef {Name=name,Hash=name.Substring(4,64).ToLowerInvariant()};
                // The reserved filename and its content hash identify an NCP copy,
                // even after reinstalling NCP or deleting a preset/local backup.
                try {
                    if(IccLibrary.Hash(IccLibrary.ReadBytes(path))==profile.Hash)result.Add(path);
                }
                catch(InvalidDataException) { }
                catch(IOException) { }
            }
            return result;
        }
        public void CleanupUnused(IEnumerable<string> protectedNames)
        {
            List<string> candidates;
            try {candidates=UnusedCopies(protectedNames);}
            catch(UnauthorizedAccessException) {RunElevatedCleanup(protectedNames);return;}
            catch(System.Security.SecurityException) {RunElevatedCleanup(protectedNames);return;}
            if(candidates.Count==0)return;
            foreach(string path in candidates) {
                bool removed=UninstallColorProfileW(null,path,true);
                int error=Marshal.GetLastWin32Error();
                if(!File.Exists(path))continue;
                if(removed)throw new IOException("Windows에서 제거한 NCP ICC 사본이 아직 남아 있습니다: "+Path.GetFileName(path));
                if(error!=5 && error!=1314)throw new Win32Exception(error,"사용하지 않는 NCP ICC 제거에 실패했습니다.");
                RunElevatedCleanup(protectedNames);return;
            }
        }
        void RunElevatedCleanup(IEnumerable<string> protectedNames)
        {
            var start=new ProcessStartInfo {FileName=typeof(WindowsIccBackend).Assembly.Location,
                Arguments="--clean-icc "+WindowsIdentity.GetCurrent().User.Value,UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            try {using(var process=Process.Start(start)) {process.WaitForExit();if(process.ExitCode!=0)throw new InvalidOperationException("NCP ICC 정리를 완료하지 못했습니다. (오류 "+process.ExitCode+")");}}
            catch(Win32Exception e) {if(e.NativeErrorCode==1223)throw new InvalidOperationException("NCP ICC 정리 권한 요청을 취소했습니다.",e);throw;}
            if(UnusedCopies(protectedNames).Count>0)throw new IOException("사용하지 않는 NCP ICC 사본이 남아 있습니다.");
        }
        public static int CleanupFromCommandLine(string userSid)
        {
            try {
                if(WindowsIdentity.GetCurrent().User.Value!=userSid)return 5;
                var data=new Store(AppDomain.CurrentDomain.BaseDirectory).Load();
                var keep=data.IccOriginals.SelectMany(o=>(o.State.Profiles??new List<string>()).Concat(new[]{o.State.ProfileName}))
                    .Where(n=>n!=null && !IsNcpProfile(n));
                foreach(string path in UnusedCopies(keep)) {
                    bool removed=UninstallColorProfileW(null,path,true);int error=Marshal.GetLastWin32Error();
                    if(File.Exists(path))return removed?1:Math.Max(1,error);
                }
                return 0;
            }catch {return 2;}
        }
    }
    public sealed class DemoIccBackend:IIccBackend
    {
        public IccState Current=new IccState {PerUser=false,ProfileName="sRGB Color Space Profile.icm"};
        public IccState Read(DisplayTarget display) {return new IccState {PerUser=Current.PerUser,ProfileName=Current.ProfileName};}
        public string EnsureInstalled(string path,IccProfileRef profile) {return Path.GetFileName(path);}
        public void Apply(DisplayTarget display,string name,IEnumerable<string> managed) {Current=new IccState {PerUser=true,ProfileName=name};}
        public void Restore(DisplayTarget display,IccState state,IEnumerable<string> managed,bool exact=false) {Current=new IccState {PerUser=state.PerUser,ProfileName=state.ProfileName};}
        public void CleanupUnused(IEnumerable<string> protectedNames) {}
    }
}
