using System;
using System.IO;
using System.Linq;

namespace Ncp
{
    internal interface IIccUserProfileAccess
    {
        bool ReadNative(string deviceKey);
        void WriteNative(string deviceKey,bool enabled);
        bool? ReadStored(string path);
        void WriteStored(string path,bool enabled);
    }
    internal sealed class IccUserProfileSetting
    {
        const string MonitorClass="{4d36e96e-e325-11ce-bfc1-08002be10318}";
        const string DevicePrefix=@"\Registry\Machine\System\CurrentControlSet\Control\Class\"+MonitorClass+@"\";
        readonly IIccUserProfileAccess access;
        internal IccUserProfileSetting(IIccUserProfileAccess access) {this.access=access;}
        internal static string StoredPath(string key)
        {
            if(key==null || !key.StartsWith(DevicePrefix,StringComparison.OrdinalIgnoreCase))return null;
            string slot=key.Substring(DevicePrefix.Length);
            if(slot.Length!=4 || !slot.All(c=>c>='0' && c<='9'))return null;
            return @"Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\"+MonitorClass+@"\"+slot;
        }
        internal bool Read(string key)
        {
            string path=StoredPath(key);
            bool? stored=path==null?null:access.ReadStored(path);
            return stored??access.ReadNative(key);
        }
        internal void Set(string key,bool enabled)
        {
            // Re-enabling an already enabled WCS scope can reset its association list.
            if(Read(key)==enabled)return;
            Exception failure=null;
            try {access.WriteNative(key,enabled);}catch(Exception e) {failure=e;}
            if(Read(key)==enabled)return;
            if(failure!=null)throw failure;
            // Some Windows configurations report success without persisting the
            // monitor's checkbox. Repair only this validated monitor's ICM flag.
            string path=StoredPath(key);
            if(path==null)throw new InvalidOperationException("사용자별 ICC 설정 변경 결과가 일치하지 않습니다.");
            access.WriteStored(path,enabled);
            if(Read(key)!=enabled)throw new IOException("사용자별 ICC 설정을 저장하지 못했습니다.");
        }
    }
}
