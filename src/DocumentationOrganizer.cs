using System;
using System.IO;

namespace TelemetryMonitor
{
    internal static class DocumentationOrganizer
    {
        // An upgrade may leave old Markdown files at the installation root. Keep every original.
        internal static void Organize(string root)
        {
            string directory=Path.GetFullPath(root);
            string prefix=directory.EndsWith(Path.DirectorySeparatorChar.ToString(),StringComparison.Ordinal)?directory:directory+Path.DirectorySeparatorChar;
            string[] sources=Directory.GetFiles(directory,"*.md",SearchOption.TopDirectoryOnly);
            if(sources.Length==0)return;
            string documents=Path.Combine(directory,"说明");EnsureDirectory(documents);
            foreach(string source in sources)
            {
                string target=Path.Combine(documents,Path.GetFileName(source));
                if(File.Exists(target))
                {
                    string archive=Path.Combine(documents,"历史根目录文件");EnsureDirectory(archive);target=Path.Combine(archive,Path.GetFileName(source));
                    if(File.Exists(target))target=Path.Combine(archive,Path.GetFileNameWithoutExtension(source)+"_"+Guid.NewGuid().ToString("N")+".md");
                }
                if(!Path.GetFullPath(target).StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("Documentation target is outside the installation directory");
                File.Move(source,target);
            }
        }
        private static void EnsureDirectory(string path)
        {
            Directory.CreateDirectory(path);
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Documentation folders cannot be directory links");
        }
    }
}
