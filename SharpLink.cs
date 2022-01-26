using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace de.usd.SharpLink
{
    /**
     * SharpLink v1.0.0
     * 
     * This namespace contains classes that allow low privileged user accounts to create
     * file system and registry symbolic links.
     *
     * File system symbolic links created by functions from this namespace are pseudo-links
     * that consist out of the combination of a Junction with an object manager symbolic link
     * in the '\RPC Control' object directory. This technique was publicized by James Forshaw
     * and implemented within his symboliclink-testing-tools:
     *
     *      - https://github.com/googleprojectzero/symboliclink-testing-tools)
     *
     * We used James's implementation as a reference for the classes implemented in this namespace.
     * Moreover, the C# code for creating the junctions was mostly copied from these resources:
     *
     *      - https://gist.github.com/LGM-AdrianHum/260bc9ab3c4cd49bc8617a2abe84ca74
     *      - https://coderedirect.com/questions/136750/check-if-a-file-is-real-or-a-symbolic-link
     *
     * Also the implementation of registry symbolic links is very close to the one within the
     * symboliclink-testing-tools and credits go to James again. Furthermore, the following
     * resource was used as a reference:
     *
     *      - https://bugs.chromium.org/p/project-zero/issues/detail?id=872
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    [StructLayout(LayoutKind.Sequential)]
    struct KEY_VALUE_INFORMATION
    {
        public uint TitleIndex;
        public uint Type;
        public uint DataLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x400)]
        public byte[] Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUNT_POINT_REPARSE_BUFFER
    {
        public ushort SubstituteNameOffset;
        public ushort SubstituteNameLength;
        public ushort PrintNameOffset;
        public ushort PrintNameLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x3FF0)]
        public byte[] PathBuffer;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct REPARSE_DATA_BUFFER
    {
        [FieldOffset(0)] public uint ReparseTag;
        [FieldOffset(4)] public ushort ReparseDataLength;
        [FieldOffset(6)] public ushort Reserved;
        [FieldOffset(8)] public MOUNT_POINT_REPARSE_BUFFER MountPointBuffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OBJECT_ATTRIBUTES : IDisposable
    {
        public int Length;
        public IntPtr RootDirectory;
        private IntPtr objectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;

        public OBJECT_ATTRIBUTES(string name, uint attrs)
        {
            Length = 0;
            RootDirectory = IntPtr.Zero;
            objectName = IntPtr.Zero;
            Attributes = attrs;
            SecurityDescriptor = IntPtr.Zero;
            SecurityQualityOfService = IntPtr.Zero;

            Length = Marshal.SizeOf(this);
            ObjectName = new UNICODE_STRING(name);
        }

        public UNICODE_STRING ObjectName
        {
            get
            {
                return (UNICODE_STRING)Marshal.PtrToStructure(
                 objectName, typeof(UNICODE_STRING));
            }

            set
            {
                bool fDeleteOld = objectName != IntPtr.Zero;
                if (!fDeleteOld)
                    objectName = Marshal.AllocHGlobal(Marshal.SizeOf(value));
                Marshal.StructureToPtr(value, objectName, fDeleteOld);
            }
        }

        public void Dispose()
        {
            if (objectName != IntPtr.Zero)
            {
                Marshal.DestroyStructure(objectName, typeof(UNICODE_STRING));
                Marshal.FreeHGlobal(objectName);
                objectName = IntPtr.Zero;
            }
        }
    }

    public struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Buffer;

        public UNICODE_STRING(string str)
        {
            Length = (ushort)(str.Length * 2);
            MaximumLength = (ushort)((str.Length * 2) + 1);
            Buffer = str;
        }
    }

    /**
     * The ILink interface contains the required methods that classes need to implement to be assignable
     * to a LinkGroup. Currently, this interface is implemented by the Symlink and RegistryLink types.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public interface ILink
    {
        // open the underlying link
        void Open();

        // close the underlying link
        void Close();

        // print the current link status to stdout
        void Status();

        // enforce closing the link
        void ForceClose();

        // tell the link whether it should stay alive after the object is cleaned up
        void KeepAlive(bool value);
    }

    /**
     * A LinkGroup represents a collection of symbolic links and can be used to perform compound
     * operations on them. This is useful when you have multiple links that you want to Open or
     * Close at the same time. The group can store all kind of links that implement the ILink
     * interface.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class LinkGroup
    {
        // Links stored within the LinkGroup
        private HashSet<ILink> links;

        /**
         * On instantiation, a LinkGroup obtains a fresh HashSet to store it's links in.
         */
        public LinkGroup()
        {
            links = new HashSet<ILink>();
        }

        /**
         * Adds an already existing Link to the group.
         *
         * @param link  already existing Link to add
         */
        public void AddLink(ILink link)
        {
            links.Add(link);
        }

        /**
         * Create a new Symlink from the specified path to the specified target and assign
         * it to the LinkGroup.
         *
         * @param path  path the symlink should be created from
         * @param target  target the symlink should be pointing to
         */
        public void AddSymlink(string path, string target)
        {
            AddSymlink(path, target, false);
        }

        /**
         * Create a new Symlink from the specified path to the specified target and assign
         * it to the LinkGroup. This version of the function allows to set the keepAlive
         * property of the link.
         *
         * @param path  path the symlink should be created from
         * @param target  target the symlink should be pointing to
         * @param keepAlive  whether to keep the symlink alive after the object is cleaned up
         */
        public void AddSymlink(string path, string target, bool keepAlive)
        {
            links.Add(new Symlink(path, target, keepAlive));
        }

        /**
         * Create a new RegistryLink from the specified key to the specified target key and assign
         * it to the LinkGroup.
         *
         * @param key  key the RegistryLink should be created from
         * @param target  target the RegistryLink should be pointing to
         */
        public void AddRegistryLink(string path, string target)
        {
            AddRegistryLink(path, target, false);
        }

        /**
         * Create a new RegistryLink from the specified key to the specified target key and assign
         * it to the LinkGroup. This version of the function allows to set the keepAlive
         * property of the RegistryLink.
         *
         * @param key  key the RegistryLink should be created from
         * @param target  target the RegistryLink should be pointing to
         * @param keepAlive  whether to keep the symlink alive after the object is cleaned up
         */
        public void AddRegistryLink(string key, string target, bool keepAlive)
        {
            links.Add(new RegistryLink(key, target, keepAlive));
        }

        /**
         * Tells all contained Links that they should stay alive, even after the object
         * was cleaned up.
         */
        public void KeepAlive()
        {
            KeepAlive(true);
        }

        /**
         * Tells all contained Links whether they should stay alive, even after the object
         * was cleaned up.
         *
         * @param value  wether or not the Symlinks should stay alive
         */
        public void KeepAlive(bool value)
        {
            foreach (ILink link in links)
                link.KeepAlive(value);
        }

        /**
         * Open all Links contained within this group.
         */
        public void Open()
        {
            foreach (ILink link in links)
                link.Open();
        }

        /**
         * Close all Links contained within this group.
         */
        public void Close()
        {
            foreach (ILink link in links)
                link.Close();
        }

        /**
         * Enforce the Close operation for all Links contained within this group.
         */
        public void ForceClose()
        {
            foreach (ILink link in links)
                link.ForceClose();
        }

        /**
         * Remove all Links stored in this group. Depending on their keepAlive
         * setting, the Links are only removed from the group, but not closed.
         */
        public void Clear()
        {
            links.Clear();
        }

        /**
         * Return the Links stored in this group as an array.
         *
         * @return ILink array of the contained Links
         */
        public ILink[] GetLinks()
        {
            return links.ToArray<ILink>();
        }

        /**
         * Print some status information on the current group. This includes the number of
         * contained Links and the detailes of them.
         */
        public void Status()
        {
            Console.WriteLine("[+] LinkGroup contains {0} link(s):", links.Count);

            foreach (ILink link in links)
            {
                Console.WriteLine("[+]");
                link.Status();
            }
        }
    }

    /**
     * An instance of  Symlink represents a single file system symbolic link. Each Symlink contains
     * the path the Symlink should be cretaed in and the target it should be pointing to. Creating
     * the Symlink object does not open it already on the file system. The Open function needs to
     * be called to achieve this. Symlinks are removed when the corresponding Symlink object goes
     * out of scope. This default behavior can be modified by using the keepAlive function.
     *
     * When opening a Symlink, it attempts to create one Junction and one DosDevice that are needed to
     * setup the Symlink on the file system. Before doing so, it checks whether an approtiate Junction
     * or DosDevice already exists. Only if not existing, the objects are created. After creation, the
     * objects are associated to Symlink object. The Symlink is then the owner of these objects and
     * responsible for maintaining their lifetime. If the objects already existed, the Symlink does not
     * take ownership of them.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class Symlink : ILink
    {
        // file system path the symlink is created in
        private string path;

        // file system path the symlink should point to
        private string target;

        // associated Junction object (assigned when opening the link - may be null)
        private Junction junction;

        // associated DosDevice object (assigned when opening the link - may be null)
        private DosDevice dosDevice;

        // whether to keep the associated Junction and DosDevice alive after the object is removed
        private bool keepAlive;

        /**
         * Symlinks are created by specifying the location they should be created in and the location
         * they should point to.
         *
         * @param path  file system path the link is created in
         * @param target  file system path the link is pointing to
         */
        public Symlink(string path, string target) : this(path, target, false) { }

        /**
         * Symlinks are created by specifying the location they should be created in and the location
         * they should point to. Additionally, this constructor allows specifying the keepAlive value
         * of the link, which determines whether the physical link should be kept alive after the
         * object is gone.
         *
         * @param path  file system path the link is created in
         * @param target  file system path the link is pointing to
         * @param keepAlive  whether to keep the physical link alive after object cleanup
         */
        public Symlink(string path, string target, bool keepAlive)
        {
            this.path = Path.GetFullPath(path);
            this.target = Path.GetFullPath(target);

            this.junction = null;
            this.dosDevice = null;
            this.keepAlive = keepAlive;
        }

        /**
         * Set the keepAlive property to true and tell already existing Junction and DosDevice objects
         * to stay alive after cleanup.
         */
        public void KeepAlive()
        {
            KeepAlive(true);
        }

        /**
         * Set the keepAlive property and tell already existing Junction and DosDevice objects whether to
         * stay alive after cleanup.
         *
         * @param value  whether to keep the physical link alive after object cleanup
         */
        public void KeepAlive(bool value)
        {
            this.keepAlive = true;

            if (junction != null)
                junction.KeepAlive(value);

            if (dosDevice != null)
                dosDevice.KeepAlive(value);
        }

        /**
         * Return the Junction object stored in this link. Links only have a Junction object set when they
         * are open and a corresponding Junction does not already exist.
         */
        public Junction GetJunction()
        {
            return junction;
        }

        /**
         * Return the DosDevice object stored in this link. Links only have a DosDevice object set when they
         * are open and a corresponding DosDevice does not already exist.
         */
        public DosDevice GetDosDevice()
        {
            return dosDevice;
        }

        /**
         * Print some status information on the link. This includes the link path and target path as well as
         * the associated Junction and DosDevice.
         */
        public void Status()
        {
            Console.WriteLine("[+] Link type: File system symbolic link");
            Console.WriteLine("[+] \tLink path: {0}", path);
            Console.WriteLine("[+] \tTarget path: {0}", target);

            Console.WriteLine("[+] \tAssociated Junction: {0}", (junction == null) ? "none" : junction.GetBaseDir());
            Console.WriteLine("[+] \tAssociated DosDevice: {0}", (dosDevice == null) ? "none" : dosDevice.GetName());
        }

        /**
         * Checks whether a target was specified and open all Junctions and DosDevices that were
         * configured for this container.
         */
        public void Open()
        {
            if (junction != null && dosDevice != null)
            {
                Console.WriteLine("[-] Symlink was already opened. Call the Close function first if you want to reopen.");
                return;
            }

            string linkFile = Path.GetFileName(path);
            string linkDir = Path.GetDirectoryName(path);

            if (String.IsNullOrEmpty(linkDir))
            {
                Console.WriteLine("[-] Symlinks require at least one upper directory (e.g. example\\link)");
                return;
            }

            if (junction == null)
                junction = Junction.Create(linkDir, @"\RPC CONTROL", keepAlive);

            if (dosDevice == null)
                dosDevice = DosDevice.Create(linkFile, target, keepAlive);

            Console.WriteLine("[+] Symlink setup successfully.");
        }

        /**
         * Closes all Junctions and DosDevices configured for this container. The corresponding object
         * attributes are set to null afterwards, to distinguish the link from an open one.
         */
        public void Close()
        {
            if (junction == null && dosDevice == null)
            {
                Console.WriteLine("[!] The current Symlink does not hold ownership on any Junction or DosDevice.");
                Console.WriteLine("[!] Use ForceClose if you really want to close it.");
                return;
            }

            if (junction != null)
                junction.Close();

            if (dosDevice != null)
                dosDevice.Close();

            junction = null;
            dosDevice = null;

            Console.WriteLine("[+] Symlink deleted.");
        }

        /**
         * Enforces the Close operation on all Junctions and DosDevices configured for this container.
         * The corresponding object attributes are set to null afterwards, to distinguish the link
         * from an open one.
         */
        public void ForceClose()
        {
            if (junction != null)
                junction.ForceClose();

            if (dosDevice != null)
                dosDevice.ForceClose();

            junction = null;
            dosDevice = null;

            Console.WriteLine("[+] Symlink deleted.");
        }

        /**
         * Symlink objects may be stored in LinkGroups, which store them internally in a HashSet. This
         * requires the type to be hashable. This function builds a HashCode consisting out of the link
         * path and the target.
         */
        public override int GetHashCode()
        {
            return (path + " -> " + target).GetHashCode();
        }

        /**
         * Equals wrapper.
         *
         * @param obj object to compare with
         */
        public override bool Equals(object obj)
        {
            return Equals(obj as Symlink);
        }

        /**
         * Two Symlinks are equal if their path and target are matching.
         *
         * @param other Symlink to compare with
         */
        public bool Equals(Symlink other)
        {
            return (path == other.path) && (target == other.target);
        }

        /**
         * Create a Symlink from an existing file.
         *
         * @param path  file system path to the existing file
         * @param target  symlink target
         * @return Symlink with the requested properties
         */
        public static Symlink FromFile(string path, string target)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine("[-] Unable to find file: {0}", path);
                return null;
            }

            Console.Write("[?] Delete existing file? (y/N) ");
            ConsoleKey response = Console.ReadKey(false).Key;
            Console.WriteLine();

            if (response == ConsoleKey.Y)
                File.Delete(path);

            return new Symlink(path, target);
        }

        /**
         * Create a Symlink for each file existing in the specified folder. All created
         * Symlinks share the same target and are bundeled within a LinkGroup.
         *
         * @param src  file system path to the folder to create symlinks from
         * @param target  shared target for all created symlinks
         * @return LinkGroup containing the requested Symlinks
         */
        public static LinkGroup FromFolder(string src, string target)
        {
            if (!Directory.Exists(src))
            {
                Console.WriteLine("[-] Unable to find directory: {0}", src);
                return null;
            }

            Console.Write("[?] Delete files in link folder? (y/N) ");
            ConsoleKey response = Console.ReadKey(false).Key;
            Console.WriteLine();

            LinkGroup linkGroup = new LinkGroup();

            foreach (string filename in Directory.EnumerateFiles(src))
            {
                if (response == ConsoleKey.Y)
                    File.Delete(filename);

                linkGroup.AddLink(new Symlink(filename, target));
            }

            return linkGroup;
        }

        /**
         * Create a Symlink for each file existing in the specified folder. The target
         * for each created Symlink is a file with the same name as the link file within
         * the specified target directory. The created Symlinks are bundeled into a
         * LinkGroup.
         *
         * @param src  file system path to the folder to create symlinks from
         * @param dst  target directory where the symlinks are pointing to
         * @return LinkGroup containing the requested Symlinks
         */
        public static LinkGroup FromFolderToFolder(string src, string dst)
        {
            if (!Directory.Exists(src))
            {
                Console.WriteLine("[-] Unable to find directory: {0}", src);
                return null;
            }

            if (!Directory.Exists(dst))
            {
                Console.WriteLine("[-] Unable to find directory: {0}", dst);
                return null;
            }

            Console.Write("[?] Delete files in link folder? (y/N) ");
            ConsoleKey response = Console.ReadKey(false).Key;
            Console.WriteLine();

            LinkGroup linkGroup = new LinkGroup();

            foreach (string filename in Directory.EnumerateFiles(src))
            {
                if (response == ConsoleKey.Y)
                    File.Delete(filename);

                linkGroup.AddLink(new Symlink(filename, Path.Combine(dst, Path.GetFileName(filename))));
            }

            return linkGroup;
        }
    }

    /**
     * The DosDevice class is used for creating mappings between the RPC Control object directory
     * and the file system. These mappings are required for creating the pseudo file system links.
     * DosDevices are treated as resource and are automatically removed after the associated object
     * was cleaned up. This can be prevented by using the KeepAlive function.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class DosDevice
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

        // name of the DosDevice
        private string name;

        // path to the target file on the file system with the \??\ prefix
        private string target;

        // whether the DosDevice was already manually closed
        private bool closed;

        // whether to keep the created DosDevice alive after the object is cleaned up
        private bool keepAlive;

        private const uint DDD_RAW_TARGET_PATH = 0x00000001;
        private const uint DDD_REMOVE_DEFINITION = 0x00000002;
        private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;
        private const uint DDD_NO_BROADCAST_SYSTEM = 0x00000008;

        /**
         * DosDevices should be created using the Create function of this class. The Create
         * function verifies that the requested DosDevice does not already exist before creating
         * it. If this is the case and the DosDevice does not exist, the Create function uses
         * this Constructor to instantiate the DosDevice.
         *
         * @param name  name of the DosDevice
         * @param target  file system path to the target of the DosDevice
         * @param keepAlive  whether to keep the DosDevice alive after object cleanup
         */
        private DosDevice(string name, string target, bool keepAlive)
        {
            this.name = name;
            this.target = target;
            this.keepAlive = keepAlive;

            this.closed = false;
        }

        /**
         * If keepAlive was not set to true, cleanup the DosDevice when the object is removed.
         */
        ~DosDevice()
        {
            if (!keepAlive && !closed)
                Close();
        }

        /**
         * Get the target of the DosDevice.
         *
         * @return configured target of the DosDevice
         */
        public string GetTarget()
        {
            return target;
        }

        /**
         * Get the name of the DosDevice.
         *
         * @return configured name of the DosDevice
         */
        public string GetName()
        {
            return name;
        }

        /**
         * Set the keepAlive property to the specified value.
         *
         * @param value  whether to keep the DosDevice alive after object cleanup
         */
        public void KeepAlive(bool value)
        {
            keepAlive = value;
        }

        /**
         * Cleanup the DosDevice. This is basically a wrapper around the static Close
         * function.
         */
        public void Close()
        {
            Close(name, target);
            closed = true;
        }

        /**
         * Enforce cleanup of the DosDevice. This is basically a wrapper around the static Close
         * function.
         */
        public void ForceClose()
        {
            Close(name);
            closed = true;
        }

        /**
         * Create a new DosDevice with the specified name, pointing to the specified location.
         * This function should be used to create DosDevice objects, as it checks whether the
         * requested DosDevice already exists before creating it. If non existing, the DosDevice
         * is created and a corresponding object is returned by this function. If the DosDevice
         * does already exist, null is returned.
         *
         * @param name  name of the DosDevice
         * @param target  file system path the DosDevice is pointing to
         * @param keepAlive  whether to keep the DosDevice alive after object cleanup
         * @return newly created DosDevice or null
         */
        public static DosDevice Create(string name, string target, bool keepAlive)
        {
            name = PrepareDevicePath(name);
            target = PrepareTargetPath(target);

            string destination = GetTarget(name);

            if (destination != null)
            {
                if (destination == target)
                {
                    Console.WriteLine("[+] DosDevice {0} -> {1} does already exist.", name, target);
                    return null;
                }

                throw new IOException(String.Format("DosDevice {0} does already exist, but points to {0}", name, destination));
            }

            Console.WriteLine("[+] Creating DosDevice: {0} -> {1}", name, target);

            if (DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH, name, target) &&
                DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH, name, target))
            {
                return new DosDevice(name, target, keepAlive);
            }

            throw new IOException("Unable to create DosDevice.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }

        /**
         * Close the specified DosDevice. A DosDevice is only closed if it's target path patches the target specified
         * during the function call. Otherwise, a warning is printed and the device is treated as closed, without
         * actually closing it.
         *
         * @param name  name of the DosDevice to close
         * @param target  file system path the DosDevice points to
         */
        public static void Close(string name, string target)
        {
            name = PrepareDevicePath(name);
            target = PrepareTargetPath(target);

            string destination = GetTarget(name);

            if (destination == null)
            {
                Console.WriteLine("[+] DosDevice {0} -> {1} was already closed.", name, target);
                return;
            }

            if (destination != target)
            {
                Console.WriteLine("[!] DosDevice {0} is pointing to {1}.", name, destination);
                Console.WriteLine("[!] Treating as closed.");
                return;
            }

            Console.WriteLine("[+] Deleting DosDevice: {0} -> {1}", name, target);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, name, target);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, name, target);
        }

        /**
         * Simplified version of the Close function that does not perform a target check.
         *
         * @param name  name of the DosDevice to close
         * @param target  file system path the DosDevice points to
         */
        public static void Close(string name)
        {
            name = PrepareDevicePath(name);

            Console.WriteLine("[+] Deleting DosDevice: {0}", name);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, name, null);

            DefineDosDevice(DDD_NO_BROADCAST_SYSTEM | DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION |
                            DDD_EXACT_MATCH_ON_REMOVE, name, null);
        }

        /**
         * Get the target of the specified DosDevice name.
         *
         * @param name  name of the DosDevice to obtain the target from
         */
        public static string GetTarget(string name)
        {
            name = PrepareDevicePath(name);

            StringBuilder pathInformation = new StringBuilder(250);
            uint result = QueryDosDevice(name, pathInformation, 250);

            if (result == 0)
                return null;

            return pathInformation.ToString();
        }

        /**
         * DosDevices created by this class are expected to originate from the RPC Control object directory.
         * This function applies the corresponding prefix to the specified DosDevice path, if required. If
         * the prefix is already used, the path is returned without modification.
         * 
         * @param path  DosDevice path
         * @return prefixed path if prefixing was necessary, the original path otherwise
         */
        private static string PrepareDevicePath(string path)
        {
            string prefix = @"Global\GLOBALROOT\RPC CONTROL\";

            if (path.StartsWith(prefix))
                return path;

            return prefix + path;
        }

        /**
         * Target file system paths of DosDevices require the '\??\' prefix. This function applies the
         * prefix if not already applied.
         * 
         * @param path  file system path
         * @return prefixed path if prefixing was necessary, the original path otherwise
         */
        private static string PrepareTargetPath(string path)
        {
            string prefix = @"\??\";

            if (path.StartsWith(prefix))
                return path;

            return prefix + path;
        }
    }

    /**
     * The Junction class is used for creating file system junctions from C#. Together with
     * DosDevices, Junctions are used to build pseudo symbolic links on the file system.
     * Junctions are treated as resources and are automatically cleaned up after the corresponding
     * object is deleted. This default bahvior can be changed by using the KeepAlive function.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class Junction
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateFile(string filename, FileAccess access, FileShare share, IntPtr securityAttributes, FileMode fileMode, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        // base directory the junction starts from
        private string baseDir;

        // target directory the junction is pointing to
        private string targetDir;

        // whether to keep the associated Junction alive when the object is cleaned up
        private bool keepAlive;

        // whether the DosDevice was already closed manually
        private bool closed;

        // whether the junction directory was created by this instance
        private bool dirCreated;

        private const int FSCTL_SET_REPARSE_POINT = 0x000900A4;
        private const int FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const int FSCTL_DELETE_REPARSE_POINT = 0x000900AC;
        private const uint ERROR_NOT_A_REPARSE_POINT = 0x80071126;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        /**
         * Junction objects should be created by the static Create function. The Create function
         * first verifies whether the corresponding Junction exists on the file system. Only if
         * this is not the case, the Junction object is created by using this constructor.
         *
         * @param baseDir  base directory the Junction originates from
         * @param targetDir  target directory the Junction is pointing to
         * @param dirCreated  whether the baseDir was created during Junction creation
         * @param keepAlive  whether to keep the Junction alive after object cleanup
         */
        private Junction(string baseDir, string targetDir, bool dirCreated, bool keepAlive)
        {
            this.baseDir = baseDir;
            this.targetDir = targetDir;

            this.dirCreated = dirCreated;
            this.keepAlive = keepAlive;

            this.closed = false;
        }

        /**
         * If the keepAlive property was not set to true, remove the underlying Junction during
         * object cleanup.
         */
        ~Junction()
        {
            if (!keepAlive && !closed)
                Close();
        }

        /**
         * Return the base directory of the junction.
         *
         * @return base directory of the junction
         */
        public string GetBaseDir()
        {
            return baseDir;
        }

        /**
         * Return the target directory of the junction.
         *
         * @return target directory of the junction
         */
        public string GetTargetDir()
        {
            return targetDir;
        }

        /**
         * Set the keepAlive property of the Junction object to the specified value.
         *
         * @param value  whether to keep the Junction alive after object cleanup
         */
        public void KeepAlive(bool value)
        {
            keepAlive = value;
        }

        /**
         * Wrapper around the static Close function that performs the actual operation.
         * If the Junction's baseDir was created by this object, remove it too.
         */
        public void Close()
        {
            Close(baseDir, targetDir);
            closed = true;

            if (Directory.Exists(baseDir) && dirCreated)
                Directory.Delete(baseDir);
        }

        /**
         * Wrapper around the static Close function that performs the actual operation.
         * This version of the Close function enforces closing of the underlying Junction
         * object, even when the targetDir path does not match for the Junction located at
         * baseDir
         */
        public void ForceClose()
        {
            Close(baseDir);
            closed = true;

            if (Directory.Exists(baseDir) && dirCreated)
                Directory.Delete(baseDir);
        }

        /**
         * Create a Junction. This function first checks whether a corresponding Junction already
         * exists and uses the DeviceIoControl function to create one if this is not the case.
         * If a Junction was created by this function, it is returned as return value. Otherwise,
         * null is returned.
         *
         * @param baseDir  directory to create the junction from
         * @param targetDir  directory the junction is pointing to
         * @param keepAlive  whether to keep the Junction alive after object cleanup
         * @return Junction object if a Junction was created, null otherwise
         */
        public static Junction Create(string baseDir, string targetDir, bool keepAlive)
        {
            bool dirCreated = false;

            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
                dirCreated = true;
            }

            string existingTarget = GetTarget(baseDir);

            if (existingTarget != null)
            {
                if (existingTarget == targetDir)
                {
                    Console.WriteLine("[+] Junction {0} -> {1} does already exist.", baseDir, targetDir);
                    return null;
                }

                throw new IOException(String.Format("Junction {0} exists, but points to {1}", baseDir, existingTarget));
            }

            DirectoryInfo baseDirInfo = new DirectoryInfo(baseDir);

            if (baseDirInfo.EnumerateFileSystemInfos().Any())
            {
                Console.Write("[!] Junction directory {0} isn't empty. Delete files? (y/N) ", baseDir);
                ConsoleKey response = Console.ReadKey(false).Key;
                Console.WriteLine();

                if (response == ConsoleKey.Y)
                {
                    foreach (FileInfo file in baseDirInfo.EnumerateFiles())
                    {
                        file.Delete();
                    }
                    foreach (DirectoryInfo dir in baseDirInfo.EnumerateDirectories())
                    {
                        dir.Delete(true);
                    }
                }

                else
                    throw new IOException("Junction directory needs to be empty.");
            }

            Console.WriteLine("[+] Creating Junction: {0} -> {1}", baseDir, targetDir);

            using (SafeFileHandle safeHandle = OpenReparsePoint(baseDir))
            {
                var targetDirBytes = Encoding.Unicode.GetBytes(targetDir);
                var reparseDataBuffer = new REPARSE_DATA_BUFFER
                {
                    ReparseTag = IO_REPARSE_TAG_MOUNT_POINT,
                    ReparseDataLength = (ushort)(targetDirBytes.Length + 12),
                    MountPointBuffer = new MOUNT_POINT_REPARSE_BUFFER
                    {
                        SubstituteNameOffset = 0,
                        SubstituteNameLength = (ushort)targetDirBytes.Length,
                        PrintNameOffset = (ushort)(targetDirBytes.Length + 2),
                        PrintNameLength = 0,
                        PathBuffer = new byte[0x3ff0]
                    }
                };

                Array.Copy(targetDirBytes, reparseDataBuffer.MountPointBuffer.PathBuffer, targetDirBytes.Length);

                var inBufferSize = Marshal.SizeOf(reparseDataBuffer);
                var inBuffer = Marshal.AllocHGlobal(inBufferSize);

                try
                {
                    Marshal.StructureToPtr(reparseDataBuffer, inBuffer, false);

                    int bytesReturned;
                    var result = DeviceIoControl(safeHandle.DangerousGetHandle(), FSCTL_SET_REPARSE_POINT,
                        inBuffer, targetDirBytes.Length + 20, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);

                    if (!result)
                        throw new IOException("Unable to create Junction.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

                    return new Junction(baseDir, targetDir, dirCreated, keepAlive);
                }

                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }
        }

        /**
         * Close a Junction. The function first checks whether the Junction is open and needs to be closed.
         * If this is the case, the DeviceIoControl function is used to close it. If the Junction points to
         * an unexpected target, it isn't closed.
         *
         * @param baseDir  base directory of the Junction
         * @param targetDir  target directory of the Junction
         */
        public static void Close(string baseDir, string targetDir)
        {
            string target = GetTarget(baseDir);

            if (target == null)
            {
                Console.WriteLine("[+] Junction was already closed.");
                return;
            }

            else if (target != targetDir)
            {
                Console.WriteLine("[!] Junction {0} points to {1}", baseDir, target);
                Console.WriteLine("[!] Treating as closed.");
                return;
            }

            Close(baseDir);
        }

        /**
         * Simplified version of the Close function that skips check on the expected Junction target.
         *
         * @param baseDir base directory of the junction
         */
        public static void Close(string baseDir)
        {
            Console.WriteLine("[+] Removing Junction: {0}", baseDir);

            using (SafeFileHandle safeHandle = OpenReparsePoint(baseDir))
            {
                var reparseDataBuffer = new REPARSE_DATA_BUFFER
                {
                    ReparseTag = IO_REPARSE_TAG_MOUNT_POINT,
                    ReparseDataLength = 0,
                    MountPointBuffer = new MOUNT_POINT_REPARSE_BUFFER
                    {
                        PathBuffer = new byte[0x3ff0]
                    }
                };

                var inBufferSize = Marshal.SizeOf(reparseDataBuffer);
                var inBuffer = Marshal.AllocHGlobal(inBufferSize);

                try
                {
                    Marshal.StructureToPtr(reparseDataBuffer, inBuffer, false);

                    int bytesReturned;
                    var result = DeviceIoControl(safeHandle.DangerousGetHandle(), FSCTL_DELETE_REPARSE_POINT,
                        inBuffer, 8, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);

                    if (!result)
                        throw new IOException("Unable to delete Junction!", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                }

                finally
                {
                    Marshal.FreeHGlobal(inBuffer);
                }
            }
        }

        /**
         * Attempt to obtain the repase point from the specified file system path. This is used to determine
         * whether a Junction is open and points to the exepcetd location.
         *
         * @param baseDir  base directory of the Junction
         * @return target the junction is pointing to
         */
        public static string GetTarget(string baseDir)
        {
            if (!Directory.Exists(baseDir))
                return null;

            REPARSE_DATA_BUFFER reparseDataBuffer;

            using (SafeFileHandle fileHandle = OpenReparsePoint(baseDir))
            {
                if (fileHandle.IsInvalid)
                {
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                }

                var outBufferSize = Marshal.SizeOf(typeof(REPARSE_DATA_BUFFER));
                var outBuffer = IntPtr.Zero;

                try
                {
                    outBuffer = Marshal.AllocHGlobal(outBufferSize);
                    int bytesReturned;
                    bool success = DeviceIoControl(fileHandle.DangerousGetHandle(), FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0,
                        outBuffer, outBufferSize, out bytesReturned, IntPtr.Zero);

                    fileHandle.Close();

                    if (!success)
                    {
                        if (((uint)Marshal.GetHRForLastWin32Error()) == ERROR_NOT_A_REPARSE_POINT)
                        {
                            return null;
                        }
                        Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                    }

                    reparseDataBuffer = (REPARSE_DATA_BUFFER)Marshal.PtrToStructure(outBuffer, typeof(REPARSE_DATA_BUFFER));
                }
                finally
                {
                    Marshal.FreeHGlobal(outBuffer);
                }
            }

            if (reparseDataBuffer.ReparseTag != IO_REPARSE_TAG_MOUNT_POINT)
            {
                return null;
            }

            string target = Encoding.Unicode.GetString(reparseDataBuffer.MountPointBuffer.PathBuffer,
                reparseDataBuffer.MountPointBuffer.SubstituteNameOffset, reparseDataBuffer.MountPointBuffer.SubstituteNameLength);

            return target;
        }

        /**
         * Create a SafeFileHandle for the requested file system path.
         *
         * @param baseDir  base directory of the Junction
         * @return SafeFileHandle for the requested file system path
         */
        private static SafeFileHandle OpenReparsePoint(string baseDir)
        {
            IntPtr handle = CreateFile(baseDir, FileAccess.Read | FileAccess.Write, FileShare.None, IntPtr.Zero, FileMode.Open,
                                       FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

            if (Marshal.GetLastWin32Error() != 0)
                throw new IOException("OpenReparsePoint failed!", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

            return new SafeFileHandle(handle, true);
        }
    }

    /**
     * The RegistryLink class can be used to create symbolic links within the Windows registry.
     * Registry links are limited in their capabilities by the operating system. Therefore, it
     * is only possible to create links within the same registry hive.
     *
     * Author: Tobias Neitzel (@qtc_de)
     */
    public class RegistryLink : ILink
    {
        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern uint NtCreateKey(out IntPtr KeyHandle, uint DesiredAccess, [In] OBJECT_ATTRIBUTES ObjectAttributes, int TitleIndex, [In] string Class, int CreateOptions, out int Disposition);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern uint NtSetValueKey(SafeRegistryHandle KeyHandle, UNICODE_STRING ValueName, int TitleIndex, int Type, byte[] Data, int DataSize);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern uint NtDeleteKey(SafeRegistryHandle KeyHandle);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern uint NtOpenKeyEx(out IntPtr hObject, uint DesiredAccess, [In] OBJECT_ATTRIBUTES ObjectAttributes, int OpenOptions);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        static extern uint NtQueryValueKey(SafeRegistryHandle KeyHandle, UNICODE_STRING ValueName, uint InformationClass, out KEY_VALUE_INFORMATION ValueInformation, int size, out int sizeRequired);

        enum KEY_VALUE_INFORMATION_CLASS : uint
        {
            KeyValueBasicInformation,
            KeyValueFullInformation,
            KeyValuePartialInformation,
            KeyValueFullInformationAlign64,
            KeyValuePartialInformationAlign64,
            KeyValueLayerInformation,
            MaxKeyValueInfoClass
        }

        private const uint ATTRIBUT_FLAG_OBJ_OPENLINK = 0x00000100;
        private const uint ATTRIBUT_FLAG_CASE_INSENSITIVE = 0x00000040;

        // combines KEY_QUERY_VALUE, KEY_SET_VALUE, KEY_CREATE_LINK
        private const uint KEY_LINK_ACCESS = 0x0001 | 0x0002 | 0x0020 | 0x00010000;

        // combines KEY_QUERY_VALUE and DELETE
        private const uint KEY_READ_DELETE_ACCESS = 0x0001 | 0x00010000;

        private const int KEY_TYPE_LINK = 0x0000006;
        private const int REG_OPTION_OPEN_LINK = 0x0008;
        private const int REG_OPTION_CREATE_LINK = 0x00000002;
        private const int REG_CREATED_NEW_KEY = 0x00000001;
        private const int REG_OPENED_EXISTING_KEY = 0x00000002;

        private const string regpath = @"\Registry\";

        // path of the RegistryLink within the registry
        private string key;

        // target of the RegistryLink
        private string target;

        // whether the phyiscal registry link was created by this object
        private bool created;

        // whether to keep the registry link open after the RegistryLink instance was cleaned up
        private bool keepAlive;

        /**
         * RegistryLinks are created by specifying the location they should be created in and the location
         * they should point to.         *
         *
         * @param key  key location the RegistryLink is created in
         * @param target  target key the RegistryLink is pointing to
         */
        public RegistryLink(string key, string target) : this(key, target, false) { }

        /**
         * RegistryLinks are created by specifying the location they should be created in and the location
         * they should point to. Additionally, this constructor allows specifying the keepAlive value of
         * the link, which determines whether the physical link should be kept alive after the object is
         * gone.
         *
         * @param key  key location the RegistryLink is created in
         * @param target  target key the RegistryLink is pointing to
         * @param keepAlive  whether to keep the physical link alive after object cleanup
         */
        public RegistryLink(string key, string target, bool keepAlive)
        {
            this.key = RegPathToNative(key);
            this.target = RegPathToNative(target);
            this.keepAlive = keepAlive;

            this.created = false;
        }

        /**
         * When keepAlive was not set to true, close the physical registry links when the RegistryLink
         * object goes out of scope.
         */
        ~RegistryLink()
        {
            if (!keepAlive && created)
                Close();
        }

        /**
         * Set the keepAlive property on the RegistryKey object to true.
         */
        public void KeepAlive()
        {
            KeepAlive(true);
        }

        /**
         * Set the keepAlive property on the RegistryKey object to the specified value.
         *
         * @param value  whether to keep the registry link alive after the object was cleaned up
         */
        public void KeepAlive(bool value)
        {
            keepAlive = value;
        }

        /**
         * Return the target of the RegistryKey.
         *
         * @return target of the RegistryKey
         */
        public string GetTarget()
        {
            return target;
        }

        /**
         * Wrapper around the static Open function. Opens the RegistryKey.
         */
        public void Open()
        {
            if (created)
            {
                Console.WriteLine("[!] Link {0} was already opened. Close it before calling Open again.", key);
                return;
            }

            created = CreateLink(key, target);
        }

        /**
         * Wrapper around the static Close function. Closes the RegistryKey.
         */
        public void Close()
        {
            if (created)
            {
                DeleteLink(key, target);
                created = false;
                return;
            }

            Console.WriteLine("[!] The current RegistryLink does not hold ownership on {0}", key);
            Console.WriteLine("[!] Use ForceClose if you really want to close it.");
        }

        /**
         * Enforce closing of the RegistryKey, independent whether they were opened by
         * this object.
         */
        public void ForceClose()
        {
            DeleteKey(key);
            created = false;
        }

        /**
         * Print some information on the RegistryKey. This includes the key name, the path to the
         * target key and whether the physical registry key was created by this object.
         */
        public void Status()
        {
            Console.WriteLine("[+] Link Type: Registry symbolic link");
            Console.WriteLine("[+] \tLink key: {0}", key);
            Console.WriteLine("[+] \tTarget key: {0}", target);
            Console.WriteLine("[+] \tCreated: {0}", created);
        }

        /**
         * RegistryLinks may be stored within a LinkGroup. LinkGroups store the associated Links within
         * a HashSet, which requires RegistryLink to be hashable. The hashcode of a RegistryLinks is
         * calculated by using the key + target combination.
         *
         * @return hashcode of the RegistryLink
         */
        public override int GetHashCode()
        {
            return (key + " -> " + target).GetHashCode();
        }

        /**
         * Equals wrapper.
         *
         * @param obj  object to compare with
         */
        public override bool Equals(object obj)
        {
            return Equals(obj as RegistryLink);
        }

        /**
         * Two RegistryLinks are equal if they have the same key and the same target.
         *
         * @param other  RegistryLink to compare with
         */
        public bool Equals(RegistryLink other)
        {
            return (key == other.key) && (target == other.target);
        }

        /**
         * Create a registry symbolic link from the specified location to the requested target.
         * If the key location already exists, the user is requested whether it should be deleted.
         * If the key location is already a symbolic link, the link is left untouched.
         *
         * @param key  registry key to create the link from
         * @param target  target for the symbolic link registry key
         * @return true if the key was created by this function
         */
        public static bool CreateLink(string key, string target)
        {
            SafeRegistryHandle handle = OpenKey(key);

            if (handle == null)
                handle = CreateLink(key);

            else
            {
                string linkPath = GetLinkTarget(handle);

                if (linkPath == null)
                {
                    Console.Write("[!] Registry key {0} does already exist and is not a symlink. Delete it (y/N)? ", key);
                    ConsoleKey response = Console.ReadKey(false).Key;
                    Console.WriteLine();

                    if (response != ConsoleKey.Y)
                    {
                        handle.Dispose();
                        throw new IOException("Cannot continue without deleting the key.");
                    }

                    DeleteKey(handle, key);
                    handle.Dispose();
                    handle = CreateLink(key);
                }

                else
                {
                    handle.Dispose();

                    if (linkPath == target)
                    {
                        Console.WriteLine("[+] Registry link {0} -> {1} alreday exists.", key, target);
                        return false;
                    }

                    Console.WriteLine("[!] Registry symlink already exists but is pointing to {0}", linkPath);
                    Console.WriteLine("[!] They key is treated as open, but may point to an unintended target.");
                    return false;
                }
            }

            UNICODE_STRING value_name = new UNICODE_STRING("SymbolicLinkValue");
            byte[] data = Encoding.Unicode.GetBytes(target);

            Console.WriteLine("[+] Assigning symlink property pointing to: {0}", target);
            uint status = NtSetValueKey(handle, value_name, 0, KEY_TYPE_LINK, data, data.Length);
            handle.Dispose();

            if (status != 0)
            {
                throw new IOException(String.Format("Failure while linking {0} to {1}. NTSTATUS: {2:X}", key, target, status));
            }

            Console.WriteLine("[+] RegistryLink setup successful!");
            return true;
        }

        /**
         * Delete the specified registry link. This function also expects the target of the link as parameter
         * and compares it with the actual target during the delete process. If the targets do not match, the
         * key is not deleted.
         *
         * @param key  registry key to close
         * @param target  expected target of the registry key
         */
        public static void DeleteLink(string key, string target)
        {
            string linkTarget = GetLinkTarget(key);

            if (linkTarget == null)
            {
                Console.WriteLine("[!] Registry key {0} is no longer a symlink.", key);
                Console.WriteLine("[!] Not deleting it.");
            }

            else if (linkTarget != target)
            {
                Console.WriteLine("[!] Registry key {0} is pointing to an unexpected target: {1}.", key, linkTarget);
                Console.WriteLine("[!] Not deleting it.");
            }

            else
                DeleteKey(key);
        }

        /**
         * Delete the specified registry key.
         *
         * @param key  registry key to delete
         */
        public static void DeleteKey(string key)
        {
            using (SafeRegistryHandle handle = OpenKey(key))
            {
                if (handle == null)
                    Console.WriteLine("[!] Registry link {0} was already closed.", key);

                else
                    DeleteKey(handle, key);
            }
        }

        /**
         * Creates the specified key as a regular key within the registry. This function is not used
         * by SharpLink itself, but can be used by users directly to create subkeys that hold symlinks.
         * This is useful when you have permissions to create subkeys on a key, but missing permissions
         * to create links. In this case you can create a subkey where link creation is now allowed since
         * you are the owner of the corresponding key.
         *
         * @param key  registry key to create
         */
        public static void CreateKey(string key)
        {
            OBJECT_ATTRIBUTES obj_attr = new OBJECT_ATTRIBUTES(RegPathToNative(key), ATTRIBUT_FLAG_CASE_INSENSITIVE);
            int disposition = 0;

            Console.WriteLine("[+] Creating registry key: {0}", key);

            IntPtr handle;
            uint status = NtCreateKey(out handle, KEY_LINK_ACCESS, obj_attr, 0, null, 0, out disposition);

            if (status != 0)
                throw new IOException(String.Format("Failure while creating registry key: {0}. NTSTATUS: {1:X}", key, status));

            if (disposition == REG_CREATED_NEW_KEY)
                Console.WriteLine("[+] Registry key was successfully created.");

            else if (disposition == REG_OPENED_EXISTING_KEY)
                Console.WriteLine("[!] Registry did already exist.");

            new SafeRegistryHandle(handle, true).Dispose();
        }

        /**
         * Return the target of a registry symbolic link.
         *
         * @param key  registry key name of the link to obtain the target from
         * @return symbolic link target or null, if not a symbolic link
         */
        public static string GetLinkTarget(string key)
        {
            using (SafeRegistryHandle handle = OpenKey(key))
            {
                return GetLinkTarget(handle);
            }
        }

        /**
         * Return the target of a registry symbolic link.
         *
         * @param handle  SafeRegistryHandle of the target key
         * @return symbolic link target or null, if not a symbolic link
         */
        private static string GetLinkTarget(SafeRegistryHandle handle)
        {
            if (handle == null)
                return null;

            KEY_VALUE_INFORMATION record = new KEY_VALUE_INFORMATION
            {
                TitleIndex = 0,
                Type = 0,
                DataLength = 0,
                Data = new byte[0x400]
            };

            int length = 0;
            uint status = NtQueryValueKey(handle, new UNICODE_STRING("SymbolicLinkValue"), (uint)KEY_VALUE_INFORMATION_CLASS.KeyValuePartialInformation,
                                     out record, Marshal.SizeOf(record), out length);

            if (status == 0)
                return System.Text.Encoding.Unicode.GetString(record.Data.Take((int)record.DataLength).ToArray());

            return null;
        }

        /**
         * Delete the specified registry key.
         *
         * @param handle  handle to the registry key to delete
         * @param display  name of the key
         */
        private static void DeleteKey(SafeRegistryHandle handle, string key)
        {
            uint status = NtDeleteKey(handle);

            if (status != 0)
            {
                handle.Dispose();
                throw new IOException(String.Format("Unable to remove registry key: {0}. NTSTATUS: {1:X}", key, status));
            }

            Console.WriteLine("[+] Registry key {0} was successfully removed.", key);
        }

        /**
         * Create a new registry key.
         *
         * @param path registry key to create
         * @return SafeRegistryHandle for the created key
         */
        private static SafeRegistryHandle CreateLink(string path)
        {
            OBJECT_ATTRIBUTES obj_attr = new OBJECT_ATTRIBUTES(path, ATTRIBUT_FLAG_CASE_INSENSITIVE);
            int disposition = 0;

            Console.WriteLine("[+] Creating registry key: {0}", path);

            IntPtr handle;
            uint status = NtCreateKey(out handle, KEY_LINK_ACCESS, obj_attr, 0, null, REG_OPTION_CREATE_LINK, out disposition);

            if (status == 0)
                return new SafeRegistryHandle(handle, true);

            throw new IOException(String.Format("Failure while creating registry key: {0}. NTSTATUS: {1:X}", path, status));
        }

        /**
         * Open a SafeRegistryHandle for the specified registry path.
         *
         * @param path registry key to open the handle on
         * @return SafeRegistryHandle for the specified key
         */
        private static SafeRegistryHandle OpenKey(string path)
        {
            OBJECT_ATTRIBUTES obj_attr = new OBJECT_ATTRIBUTES(path, ATTRIBUT_FLAG_CASE_INSENSITIVE | ATTRIBUT_FLAG_OBJ_OPENLINK);

            IntPtr handle;
            uint status = NtOpenKeyEx(out handle, KEY_READ_DELETE_ACCESS, obj_attr, REG_OPTION_OPEN_LINK);

            if (status == 0)
                return new SafeRegistryHandle(handle, true);

            if (status == 0xC0000034)
                return null;

            throw new IOException(String.Format("Unable to open registry key: {0}. NTSTATUS: {1:X}", path, status));
        }

        /**
         * Translate registry paths to their native format.
         *
         * @param path  user specified registry path
         * @return native registry path
         */
        public static string RegPathToNative(string path)
        {
            if (path[0] == '\\')
            {
                return path;
            }

            if (path.StartsWith(@"HKLM\"))
            {
                return regpath + @"Machine\" + path.Substring(5);
            }

            else if (path.StartsWith(@"HKU\"))
            {
                return regpath + @"User\" + path.Substring(4);
            }

            else if (path.StartsWith(@"HKCU\"))
            {
                return regpath + @"User\" + WindowsIdentity.GetCurrent().User.ToString() + @"\" + path.Substring(5);
            }

            throw new IOException("Registry path must be absolute or start with HKLM, HKU or HKCU");
        }
    }
}
