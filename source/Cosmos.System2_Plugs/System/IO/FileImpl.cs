using IL2CPU.API.Attribs;
using IL2CPU.API;
using Cosmos.System;
using System;
using Cosmos.System.FileSystem.VFS;

namespace Cosmos.System_Plugs.System.IO
{
    // TODO A lot of these methods should be implemented using StreamReader / StreamWriter
    [Plug(Target = typeof(File))]
    public static class FileImpl
    {
        /*
         * Plug needed for the usual issue that Array can not be converted in IEnumerable... it is starting
         * to become annoying :-(
         */
        public static void WriteAllLines(string path, string[] contents)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            if (contents == null)
            {
                throw new ArgumentNullException(nameof(contents));
            }
            if (path.Length == 0)
            {
                throw new ArgumentException("Empty", nameof(path));
            }

            Global.Debugger.SendInternal("Writing contents");

            StreamWriter Writer = new(path);

            foreach (var current in contents)
            {
                Writer.WriteLine(current);
            }

            Writer.Dispose();
        }

        public static void WriteAllBytes(string path, byte[] aData)
        {
            FileStream writer = new(path, FileMode.OpenOrCreate);
            writer.Write(aData);
            writer.Dispose();
        }

        // Attributes
        public static FileAttributes GetAttributes(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            var entry = VFSManager.GetFile(path) ?? throw new global::System.IO.FileNotFoundException(path);
            return entry.GetAttributes();
        }

        public static void SetAttributes(string path, FileAttributes fileAttributes)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            var entry = VFSManager.GetFile(path) ?? throw new global::System.IO.FileNotFoundException(path);
            entry.SetAttributes(fileAttributes);
        }

        // Timestamp setters
        public static void SetCreationTime(string path, DateTime creationTime)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            var entry = VFSManager.GetFile(path) ?? throw new global::System.IO.FileNotFoundException(path);
            entry.SetCreationTime(creationTime);
        }

        public static void SetLastWriteTime(string path, DateTime lastWriteTime)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            var entry = VFSManager.GetFile(path) ?? throw new global::System.IO.FileNotFoundException(path);
            entry.SetLastWriteTime(lastWriteTime);
        }

        public static void SetLastAccessTime(string path, DateTime lastAccessTime)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            var entry = VFSManager.GetFile(path) ?? throw new global::System.IO.FileNotFoundException(path);
            entry.SetLastAccessTime(lastAccessTime);
        }

        // Timestamp getters
        public static DateTime GetCreationTime(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            var entry = VFSManager.GetFile(path) ?? throw new global::System.IO.FileNotFoundException(path);
            return entry.GetCreationTime();
        }

        public static DateTime GetLastWriteTime(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            var entry = VFSManager.GetFile(path) ?? throw new global::System.IO.FileNotFoundException(path);
            return entry.GetLastWriteTime();
        }

        public static DateTime GetLastAccessTime(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            var entry = VFSManager.GetFile(path) ?? throw new global::System.IO.FileNotFoundException(path);
            return entry.GetLastAccessTime();
        }
    }
}