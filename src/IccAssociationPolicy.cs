using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ncp
{
    internal interface IIccAssociations
    {
        IccState Read(DisplayTarget display);
        void UsePerUser(DisplayTarget display,bool enabled);
        void Associate(DisplayTarget display,string name);
        void SetDefault(DisplayTarget display,string name);
        void Remove(DisplayTarget display,string name);
    }
    internal sealed class IccAssociationPolicy
    {
        readonly IIccAssociations api;
        internal IccAssociationPolicy(IIccAssociations api) {this.api=api;}
        internal static IccState WithoutNcp(IccState state)
        {
            state.Validate();
            return new IccState {PerUser=state.PerUser,
                ProfileName=WindowsIccBackend.IsNcpProfile(state.ProfileName)?null:state.ProfileName,
                Profiles=state.Profiles==null?null:state.Profiles.Where(n=>!WindowsIccBackend.IsNcpProfile(n)).ToList()};
        }
        internal static bool ContainsNcp(IccState state)
        {
            return WindowsIccBackend.IsNcpProfile(state.ProfileName)
                || (state.Profiles??new List<string>()).Any(WindowsIccBackend.IsNcpProfile);
        }
        void RemoveManaged(DisplayTarget display,IEnumerable<string> managed,string keep)
        {
            var recorded=new HashSet<string>(managed??new string[0],StringComparer.OrdinalIgnoreCase);
            for(int pass=0;pass<8;pass++) {
                var state=api.Read(display);
                var candidates=(state.Profiles??new List<string>()).Concat(new[]{state.ProfileName})
                    .Where(n=>n!=null && !string.Equals(n,keep,StringComparison.OrdinalIgnoreCase)
                        && (recorded.Contains(n)||WindowsIccBackend.IsNcpProfile(n)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if(candidates.Count==0)return;
                foreach(string name in candidates) {
                    var before=api.Read(display);
                    if(!(before.Profiles??new List<string>()).Contains(name,StringComparer.OrdinalIgnoreCase)
                        && !string.Equals(before.ProfileName,name,StringComparison.OrdinalIgnoreCase))continue;
                    Exception failure=null;
                    try {api.Remove(display,name);}catch(Exception e) {failure=e;}
                    // WCS can remove an association and still report failure (notably 2015).
                    // Check the result instead of trusting only the return code.
                    var actual=api.Read(display);
                    bool remains=(actual.Profiles??new List<string>()).Contains(name,StringComparer.OrdinalIgnoreCase)
                        || string.Equals(actual.ProfileName,name,StringComparison.OrdinalIgnoreCase);
                    if(remains)throw new InvalidOperationException("ICC 연결이 해제되지 않았습니다: "+name+
                        (failure==null?"":"\n"+failure.Message),failure);
                }
            }
            throw new InvalidOperationException("NCP ICC 연결 목록이 계속 변경되어 정리를 완료하지 못했습니다.");
        }
        internal void Apply(DisplayTarget display,string name,IEnumerable<string> managed)
        {
            new IccState {ProfileName=name}.Validate();
            if(name==null)throw new InvalidDataException("적용할 ICC 파일이 없습니다.");
            if(!api.Read(display).PerUser)api.UsePerUser(display,true);
            if(!(api.Read(display).Profiles??new List<string>()).Contains(name,StringComparer.OrdinalIgnoreCase))api.Associate(display,name);
            RemoveManaged(display,managed,name);
            // Removing a previous association can change Windows' default choice.
            // Make the requested choice final, after all list edits have completed.
            api.SetDefault(display,name);
            var actual=api.Read(display);
            if(!actual.PerUser || !string.Equals(actual.ProfileName,name,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Windows ICC 적용 결과가 일치하지 않습니다.\n대상: "+display.GdiName+
                    "\n사용자별 설정: "+(actual.PerUser?"켜짐":"꺼짐")+"\n요청 ICC: "+name+"\n조회 ICC: "+(actual.ProfileName??"없음")+
                    "\n연결 목록: "+string.Join(", ",actual.Profiles??new List<string>()));
        }
        internal void Restore(DisplayTarget display,IccState original,IEnumerable<string> managed,bool exact=false)
        {
            original.Validate();
            if(!exact)original=WithoutNcp(original);
            if(!api.Read(display).PerUser)api.UsePerUser(display,true);
            RemoveManaged(display,managed,original.ProfileName);
            if(!original.PerUser && api.Read(display).PerUser)api.UsePerUser(display,false);
            else if(exact && original.ProfileName!=null) {
                // A failed application must roll back to its precise previous selection.
                // Normal restoration only removes NCP and lets Windows choose the original.
                var actual=api.Read(display);
                if(!string.Equals(actual.ProfileName,original.ProfileName,StringComparison.OrdinalIgnoreCase)) {
                    api.Associate(display,original.ProfileName);api.SetDefault(display,original.ProfileName);
                }
            }
            var restored=api.Read(display);
            if(restored.PerUser!=original.PerUser || (exact && original.ProfileName!=null &&
                !string.Equals(restored.ProfileName,original.ProfileName,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Windows ICC 복원 결과를 확인할 수 없습니다.");
            // No null-profile setter: removing the NCP association lets WCS select the
            // previous associated ICC, or its implicit default when none remains.
        }
    }
}
