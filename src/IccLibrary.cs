using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;

namespace Ncp
{
    public sealed class IccLibrary
    {
        public const int MaximumBytes=16*1024*1024;
        public const int MaximumPresetBytes=24*1024*1024;
        public readonly string DirectoryPath;
        public IccLibrary(string programDirectory) {DirectoryPath=Path.Combine(Path.GetFullPath(programDirectory),"icc");}
        public static string Hash(byte[] data)
        {
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();
        }
        static uint U32(byte[] bytes,int offset)
        {
            return ((uint)bytes[offset]<<24)|((uint)bytes[offset+1]<<16)|((uint)bytes[offset+2]<<8)|bytes[offset+3];
        }
        static string Signature(byte[] bytes,int offset) {return Encoding.ASCII.GetString(bytes,offset,4);}
        public static void ValidateBytes(byte[] bytes)
        {
            if(bytes==null || bytes.Length<132 || bytes.Length>MaximumBytes)throw new InvalidDataException("ICC 파일 크기가 올바르지 않습니다. 최대 16MB까지 지원합니다.");
            if(U32(bytes,0)!=bytes.Length || Signature(bytes,36)!="acsp")throw new InvalidDataException("유효한 ICC 파일이 아닙니다.");
            if(bytes[8]!=2 && bytes[8]!=4)throw new InvalidDataException("ICC 버전 2와 4만 지원합니다.");
            if((Signature(bytes,12)!="mntr" && Signature(bytes,12)!="spac") || Signature(bytes,16)!="RGB ")
                throw new InvalidDataException("RGB 디스플레이용 ICC 프로필만 사용할 수 있습니다.");
            uint count=U32(bytes,128);
            if(count==0 || count>4096 || 132L+12L*count>bytes.Length)throw new InvalidDataException("ICC 태그 목록이 올바르지 않습니다.");
            for(int i=0;i<count;i++) {
                int offset=132+i*12; uint start=U32(bytes,offset+4),length=U32(bytes,offset+8);
                if(start<132L+12L*count || length<8 || (long)start+length>bytes.Length)
                    throw new InvalidDataException("ICC 태그 범위가 올바르지 않습니다.");
                if(Signature(bytes,offset)=="MHC2")throw new InvalidDataException("HDR/고급 색상용 MHC2 프로필은 현재 지원하지 않습니다.");
            }
        }
        public static byte[] ReadBytes(string path)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(stream.Length<132 || stream.Length>MaximumBytes)throw new InvalidDataException("ICC 파일 크기가 올바르지 않습니다.");
                var bytes=new byte[(int)stream.Length]; int read=0;
                while(read<bytes.Length) {int n=stream.Read(bytes,read,bytes.Length-read);if(n==0)throw new EndOfStreamException();read+=n;}
                ValidateBytes(bytes);return bytes;
            }
        }
        public IccProfileRef Import(string path)
        {
            string extension=Path.GetExtension(path);
            if(!extension.Equals(".icc",StringComparison.OrdinalIgnoreCase) && !extension.Equals(".icm",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ICC 또는 ICM 파일을 선택하세요.");
            return Import(Path.GetFileName(path),ReadBytes(path));
        }
        public IccProfileRef Import(string name,byte[] bytes)
        {
            ValidateBytes(bytes);
            var profile=new IccProfileRef {Hash=Hash(bytes),Name=Path.GetFileName(name)};profile.Validate();
            Directory.CreateDirectory(DirectoryPath);
            if((File.GetAttributes(DirectoryPath)&FileAttributes.ReparsePoint)!=0)throw new IOException("ICC 보관 폴더에 링크를 사용할 수 없습니다.");
            string path=PathFor(profile);
            if(File.Exists(path)) {Read(profile);return profile;}
            string temporary=Path.Combine(DirectoryPath,"."+Guid.NewGuid().ToString("N")+".tmp");
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                File.Move(temporary,path);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
            return profile;
        }
        public string PathFor(IccProfileRef profile)
        {
            profile.Validate();return Path.Combine(DirectoryPath,"NCP-"+profile.Hash+".icc");
        }
        public static bool IsBackupName(string name)
        {
            return name!=null && name.Length==72 && name.StartsWith("NCP-",StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".icc",StringComparison.OrdinalIgnoreCase)
                && name.Substring(4,64).All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'||c>='A'&&c<='F');
        }
        public int RemoveUnused(IEnumerable<IccProfileRef> references)
        {
            var retained=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var reference in references.Where(r=>r!=null)) {reference.Validate();retained.Add(reference.Hash);}
            if(!Directory.Exists(DirectoryPath))return 0;
            if((File.GetAttributes(DirectoryPath)&FileAttributes.ReparsePoint)!=0)throw new IOException("ICC 보관 폴더에 링크를 사용할 수 없습니다.");
            int removed=0;
            foreach(string file in Directory.GetFiles(DirectoryPath,"NCP-*.icc",SearchOption.TopDirectoryOnly)) {
                string path=Path.GetFullPath(file),name=Path.GetFileName(path);
                if(!string.Equals(Path.GetDirectoryName(path),DirectoryPath,StringComparison.OrdinalIgnoreCase))throw new IOException("ICC 정리 경로가 보관 폴더를 벗어났습니다.");
                if(!IsBackupName(name) || retained.Contains(name.Substring(4,64)))continue;
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)continue;
                File.Delete(path);removed++;
            }
            return removed;
        }
        public byte[] Read(IccProfileRef profile)
        {
            string path=PathFor(profile);
            if(!File.Exists(path))throw new FileNotFoundException("프로그램 폴더의 ICC 백업 파일을 찾을 수 없습니다.",path);
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("ICC 백업 파일에 링크를 사용할 수 없습니다.");
            var bytes=ReadBytes(path);
            if(Hash(bytes)!=profile.Hash)throw new InvalidDataException("ICC 백업 파일이 변경되었거나 손상되었습니다.");
            return bytes;
        }
    }
}
