using System.Data.SqlTypes;
using System.Drawing;
using System.IO;
using System.Text;
using System.Xml.Linq;
namespace File_System
{
    public class BlockNode
    {
        public byte[] Content;
        public BlockNode Next;
    }
    public class FileBocksLinkedList
    {
        public BlockNode FirstBlockNode;
        public void Append(byte[] content)
        {
            BlockNode newNode = new BlockNode();
            newNode.Content = content;
            if (FirstBlockNode == null)
                FirstBlockNode = newNode;
            else
            {
                BlockNode current = FirstBlockNode;
                BlockNode last = null;
                while (current != null)
                {
                    last = current;
                    current = current.Next;
                }
                last.Next = newNode;
            }
        }
    }
    public struct MyFile
    {
        public string Name;
        public FileBocksLinkedList FileBlocks;
        public int BlocksCount;
    }
    class DirectoryNode
    {
        public string Name;
        public bool IsDeleted = false;
        public int Size = 0;
        public int NextOffset = -1;
    }
    class FileNode
    {
        public MyFile Value;
        public bool IsDeleted;
        public int NextOffset;
    }
    class FileStreamFileSystem
    {
        // | DeletesCount (4B) |
        // | NextContainerOffset (4B) | IsDeleted (1B) | ParentDirectoryOffset (4B)|
        // if directory:
        //      | NextDirectoryInDirectoryOffset (4B) | Size (4B) | FirstDirectoryOffset (4B) | FirstFileOffset (4B) | Name | 
        // if file:
        //      | NextFileInDirectoryOffset (4B) | BlocksCount (4B) | Name | Data - Blocks Content |
        public const int FILE_BLOCK_SIZE = 1024; // 1KB
        public const int DELETES_COUNT_FOR_DEFRAGMENTATION = 5;

        public int FirstOffset;
        public int CurrentDirectoryOffset;

        private FileStream _fs;
        private BinaryReader _br;
        private BinaryWriter _bw;
        private string _containerPath;
        FileStream tempFS;
        BinaryWriter tempBW;
        BinaryReader tempBR;
        int OldCurrentDirectoryOffset; // These two are used in the defragmentation process
        int NewCurrentDirectoryOffset;

        public int DeletesCount;

        public FileStreamFileSystem(string filePath)
        {
            _containerPath = filePath;
            _fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            _br = new BinaryReader(_fs);
            _bw = new BinaryWriter(_fs);
            if (_fs.Length == 0)
            {
                DeletesCount = 0;
                _bw.Write(DeletesCount);
                // The main directory
                _bw.Write(-1); // NextContainerOffset
                _bw.Write(false); // IsDeleted
                _bw.Write(-1); // ParentDirectoryOffset
                _bw.Write(-1); // NextDirectoryInDirectoryOffset
                _bw.Write(0); // Size
                _bw.Write(-1); // FirstDirectoryOffset
                _bw.Write(-1); // FirstFileOffset
                _bw.Write("C\\"); // Name
            }
            else
            {
                DeletesCount = _br.ReadInt32();

            }
            FirstOffset = sizeof(int);
            CurrentDirectoryOffset = sizeof(int);
        }

        ~FileStreamFileSystem()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_fs == null)
                return;

            _fs.Dispose();
            _fs = null;
        }
        public void AddFile(MyFile newFile)
        {
            // | NextContainerOffset (4B) | IsDeleted (1B) | ParentDirectoryOffset |
            // | NextFileInDirectoryOffset (4B) | BlocksCount (4B) | Name | Data - Blocks Content |

            FileNode newNode = new FileNode();
            newNode.Value = newFile;
            int newNodeOffset = (int)_fs.Length;
            int currentOffset = FirstOffset;
            while (currentOffset != -1)
            {
                _fs.Position = currentOffset;
                currentOffset = _br.ReadInt32();
            }
            _fs.Position -= sizeof(int);
            _bw.Write(newNodeOffset);
            _fs.Position = newNodeOffset;
            _bw.Write(-1); // NextContainerOffset
            _bw.Write(false); // IsDeleted
            _bw.Write(CurrentDirectoryOffset); // ParentDirectoryOffset
            _bw.Write(-1); // NextFileInDirectoryOffset
            _bw.Write(newFile.BlocksCount); // BlocksCount
            _bw.Write(newFile.Name); // Name
            BlockNode currentBlock = newFile.FileBlocks.FirstBlockNode;
            while (currentBlock != null)
            {
                _bw.Write(currentBlock.Content);  // BlocksContent
                currentBlock = currentBlock.Next;
            }
            int lastFileInDirectory = GetLastElementInDirectoryOffset('f');
            if (lastFileInDirectory != -1)
            {
                _fs.Position = lastFileInDirectory + sizeof(bool) + sizeof(int) * 2;
                _bw.Write(newNodeOffset); // NextFileInDirectoryOffset for the parent directory  
            }
            else
            {
                _fs.Position = CurrentDirectoryOffset + sizeof(bool) + sizeof(int) * 5;
                _bw.Write(newNodeOffset); // FirstFileOffset for the parent directory
            }
            int parentDirectoryOffset = CurrentDirectoryOffset;
            while (parentDirectoryOffset != -1)
            {
                UpdateDirectorySize(parentDirectoryOffset, newFile.BlocksCount * FILE_BLOCK_SIZE);
                _fs.Position = parentDirectoryOffset + sizeof(bool) + sizeof(int);
                parentDirectoryOffset = _br.ReadInt32();
            }
            _fs.Flush();
        }
        public DirectoryNode GetDirectoryNode(int nodeOffset)
        {
            DirectoryNode result = new DirectoryNode();
            _fs.Position = nodeOffset + sizeof(int);
            result.IsDeleted = _br.ReadBoolean();
            _fs.Position += sizeof(int);
            result.NextOffset = _br.ReadInt32(); // NextDirectoryInDirectoryOffset
            result.Size = _br.ReadInt32();
            _fs.Position += 2 * sizeof(int);
            result.Name = _br.ReadString();
            return result;
        }
        public FileNode GetFileNode(int nodeOffset)
        {
            FileNode result = new FileNode();
            _fs.Position = nodeOffset + sizeof(int);
            result.IsDeleted = _br.ReadBoolean();
            _fs.Position += sizeof(int);
            result.NextOffset = _br.ReadInt32(); // NextFileInDirectoryOffset
            result.Value = new MyFile();
            result.Value.BlocksCount = _br.ReadInt32();
            result.Value.Name = _br.ReadString();
            result.Value.FileBlocks = new FileBocksLinkedList();
            for (int i = 0; i < result.Value.BlocksCount; i++)
            {
                result.Value.FileBlocks.Append(_br.ReadBytes(FILE_BLOCK_SIZE));
            }
            return result;
        }
        public void RemoveFileNode(int nodeOffset)
        {
            _fs.Position = nodeOffset + sizeof(int);
            _bw.Write(true); // IsDeleted
            int parentDirectoryOffset = _br.ReadInt32();
            _fs.Position += sizeof(int); // NextFileInDirectoryOffset
            int blocksCount = _br.ReadInt32();
            while (parentDirectoryOffset != -1)
            {
                UpdateDirectorySize(parentDirectoryOffset, -blocksCount * FILE_BLOCK_SIZE);
                _fs.Position = parentDirectoryOffset + sizeof(bool) + sizeof(int);
                parentDirectoryOffset = _br.ReadInt32();
            }
            DeletesCount++;
            if (DeletesCount == DELETES_COUNT_FOR_DEFRAGMENTATION)
            {
                DefragmentContainer();
                DeletesCount = 0;
            }
            _fs.Position = 0;
            _bw.Write(DeletesCount);
        }
        public void MakeDirectory(string newDirectoryName)
        {
            // | NextContainerOffset (4B) | IsDeleted (1B) | ParentDirectoryOffset (4B) |
            // | NextDirectoryInDirectoryOffset (4B) | Size (4B) | FirstDirectoryOffset (4B) | FirstFileOffset (4B) | Name |

            int newDirectoryOffset = (int)_fs.Length;
            int currentOffset = FirstOffset;
            while (currentOffset != -1)
            {
                _fs.Position = currentOffset;
                currentOffset = _br.ReadInt32();
            }
            _fs.Position -= sizeof(int);
            _bw.Write(newDirectoryOffset);

            _fs.Position = newDirectoryOffset;
            _bw.Write(-1); // NextContainerOffset
            _bw.Write(false); // IsDeleted
            _bw.Write(CurrentDirectoryOffset); // ParentDirectoryOffset
            _bw.Write(-1); // NextDirectoryInDirectoryOffset
            _bw.Write(0); // Size
            _bw.Write(-1); // FirstDirectoryOffset
            _bw.Write(-1); // FirstFileOffset
            _bw.Write(newDirectoryName); // Name

            int lastDirectory = GetLastElementInDirectoryOffset('d');
            if (lastDirectory != -1)
            {
                _fs.Position = lastDirectory + sizeof(bool) + sizeof(int) * 2;
                _bw.Write(newDirectoryOffset); // NextDirectoryInDirectoryOffset for the parent directory
            }
            else
            {
                _fs.Position = CurrentDirectoryOffset + sizeof(bool) + sizeof(int) * 4;
                _bw.Write(newDirectoryOffset); // FirstDirectoryOffset for the parent directory
            }

            _fs.Flush();
        }
        public int GetFirstElementInDirectoryOffset(char t) // type: d - directory, f - file
        {
            if (t == 'd')
            {
                _fs.Position = CurrentDirectoryOffset + sizeof(bool) + sizeof(int) * 4;
            }
            else
            {
                _fs.Position = CurrentDirectoryOffset + sizeof(bool) + sizeof(int) * 5;
            }
            return _br.ReadInt32();
        }
        public int GetLastElementInDirectoryOffset(char t)
        {
            if (t == 'd')
            {
                _fs.Position = CurrentDirectoryOffset + sizeof(bool) + sizeof(int) * 4;
            }
            else
            {
                _fs.Position = CurrentDirectoryOffset + sizeof(bool) + sizeof(int) * 5;
            }
            int currentElementOffset = _br.ReadInt32(); // firstElementOffset
            int lastElementOffset = currentElementOffset;
            while (currentElementOffset != -1)
            {
                lastElementOffset = currentElementOffset;
                _fs.Position = currentElementOffset + sizeof(bool) + sizeof(int) * 2;
                currentElementOffset = _br.ReadInt32();
            }
            return lastElementOffset;
        }
        public void ChangeDirectory(string directoryName)
        {
            switch (directoryName)
            {
                case "\\":
                    {
                        // main directory
                        CurrentDirectoryOffset = sizeof(int);
                        break;
                    }
                case "..":
                    {
                        // parent directory
                        if (CurrentDirectoryOffset != sizeof(int))
                        {
                            _fs.Position = CurrentDirectoryOffset + sizeof(bool) + sizeof(int);
                            CurrentDirectoryOffset = _br.ReadInt32();
                        }
                        break;
                    }
                default:
                    {
                        int currentOffset = GetFirstElementInDirectoryOffset('d');
                        DirectoryNode current;
                        while (currentOffset != -1)
                        {
                            current = GetDirectoryNode(currentOffset);
                            if (!current.IsDeleted && current.Name == directoryName)
                            {
                                CurrentDirectoryOffset = currentOffset;
                                break;
                            }
                            currentOffset = current.NextOffset;
                        }
                        if (currentOffset == -1)
                        {
                            Console.WriteLine($"No such folder in the current directory.");
                        }
                        return;
                    }
            }

        }
        public void RemoveDirectory(int directoryOffset)
        {
            _fs.Position = directoryOffset + sizeof(int);
            _bw.Write(true); // IsDeleted
            int parentDirectoryOffset = _br.ReadInt32();
            _fs.Position += sizeof(int); // NextDirectoryInDirectoryOffset
            int size = _br.ReadInt32();
            while (parentDirectoryOffset != -1)
            {
                UpdateDirectorySize(parentDirectoryOffset, -size);
                _fs.Position = parentDirectoryOffset + sizeof(bool) + sizeof(int);
                parentDirectoryOffset = _br.ReadInt32();
            }
            DeletesCount++;
            if (DeletesCount == DELETES_COUNT_FOR_DEFRAGMENTATION)
            {
                DefragmentContainer();
                DeletesCount = 0;
            }
            _fs.Position = 0;
            _bw.Write(DeletesCount);
        }
        public void UpdateDirectorySize(int directoryOffset, int deltaSize)
        {
            _fs.Position = directoryOffset + sizeof(bool) + sizeof(int) * 3;
            int previousSize = _br.ReadInt32();
            _fs.Position -= sizeof(int);
            _bw.Write(previousSize + deltaSize);
        }
        public void DefragmentContainer()
        {
            Console.WriteLine("Defragmentation started");
            OldCurrentDirectoryOffset = CurrentDirectoryOffset;
            string tempPath = _containerPath + ".tmp";
            tempFS = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite);
            tempBW = new BinaryWriter(tempFS);
            tempBR = new BinaryReader(tempFS);
            _fs.Position = 0;
            tempBW.Write(_br.ReadInt32()); // DeletesCount
            AddDefragmentedDirectory(FirstOffset, -1);
            _fs.Dispose();
            tempFS.Dispose();
            File.Delete(_containerPath);
            File.Move(tempPath, _containerPath);
            _fs = new FileStream(_containerPath, FileMode.Open, FileAccess.ReadWrite);
            _br = new BinaryReader(_fs);
            _bw = new BinaryWriter(_fs);
            CurrentDirectoryOffset = NewCurrentDirectoryOffset;
            Console.WriteLine("Defragmentation ended");
        }
        public void AddDefragmentedDirectory(int oldDirectoryOffset, int newParentDirectoryOffset)
        {
            // add the directory info and update current data in the new container file
            DirectoryNode oldDirectory = GetDirectoryNode(oldDirectoryOffset);
            int newDirectoryOffset = (int)tempFS.Length;
            if (OldCurrentDirectoryOffset == oldDirectoryOffset)
            {
                NewCurrentDirectoryOffset = newDirectoryOffset;
            }
            int currentOffset;
            if (newParentDirectoryOffset != -1)
            {
                tempFS.Position = newParentDirectoryOffset + 4 * sizeof(int) + sizeof(bool);
                int firstDirectoryInDirectory = tempBR.ReadInt32();
                if (firstDirectoryInDirectory == -1)
                {
                    tempFS.Position -= sizeof(int);
                    tempBW.Write(newDirectoryOffset); // FirstDirectoryOffset
                }
                else
                {
                    currentOffset = firstDirectoryInDirectory;
                    while (currentOffset != -1)
                    {
                        tempFS.Position = currentOffset + 2 * sizeof(int) + sizeof(bool);
                        currentOffset = tempBR.ReadInt32(); // NextDirectoryInDirectoryOffset
                    }
                    tempFS.Position -= sizeof(int);
                    tempBW.Write(newDirectoryOffset);
                }
                currentOffset = FirstOffset;
                while (currentOffset != -1)
                {
                    tempFS.Position = currentOffset;
                    currentOffset = tempBR.ReadInt32();
                }
                tempFS.Position -= sizeof(int);
                tempBW.Write(newDirectoryOffset);
            }
            tempFS.Position = newDirectoryOffset;
            tempBW.Write(-1);
            tempBW.Write(false);
            tempBW.Write(newParentDirectoryOffset);
            tempBW.Write(-1);
            tempBW.Write(oldDirectory.Size);
            tempBW.Write(-1);
            tempBW.Write(-1);
            tempBW.Write(oldDirectory.Name);

            int newLastElementOffset = newDirectoryOffset;
            int newFirstFileOffset = -1;
            CurrentDirectoryOffset = oldDirectoryOffset;

            // add the files from the directory
            int currentFileOffset = GetFirstElementInDirectoryOffset('f');
            FileNode currentFile;
            while (currentFileOffset != -1)
            {
                currentFile = GetFileNode(currentFileOffset);
                if (!currentFile.IsDeleted) // the file is not deleted
                {
                    if (newFirstFileOffset == -1)
                    {
                        newFirstFileOffset = (int)tempFS.Length;
                    }
                    else
                    {
                        // The last element in the new container file is a file from the directory we are adding.
                        tempFS.Position = newLastElementOffset + 2 * sizeof(int) + sizeof(bool);
                        tempBW.Write((int)tempFS.Length); // NextFileInDirectoryOffset
                    }
                    tempFS.Position = newLastElementOffset;
                    newLastElementOffset = (int)tempFS.Length;
                    tempBW.Write(newLastElementOffset);// NextContainerOffset
                    tempFS.Position = newLastElementOffset;
                    tempBW.Write(-1); // NextContainerOffset
                    tempBW.Write(false); // IsDeleted
                    tempBW.Write(newDirectoryOffset); // ParentDirectoryOffset
                    tempBW.Write(-1); // NextFileInDirectoryOffset
                    tempBW.Write(currentFile.Value.BlocksCount); // BlocksCount
                    tempBW.Write(currentFile.Value.Name); // Name
                    BlockNode currentBlock = currentFile.Value.FileBlocks.FirstBlockNode;
                    while (currentBlock != null)
                    {
                        tempBW.Write(currentBlock.Content);  // BlocksContent
                        currentBlock = currentBlock.Next;
                    }
                }
                currentFileOffset = currentFile.NextOffset;
            }
            tempFS.Position = newDirectoryOffset + 5 * sizeof(int) + sizeof(bool);
            tempBW.Write(newFirstFileOffset);

            // add the directories from the directory
            int currentDirectoryOffset = GetFirstElementInDirectoryOffset('d');
            DirectoryNode currentDirectory;
            while (currentDirectoryOffset != -1)
            {
                currentDirectory = GetDirectoryNode(currentDirectoryOffset);
                if (!currentDirectory.IsDeleted) // the directory is not deleted
                {
                    AddDefragmentedDirectory(currentDirectoryOffset, newDirectoryOffset);
                }
                currentDirectoryOffset = currentDirectory.NextOffset;
            }
        }
    }
    internal class Program
    {
        static void Main(string[] args)
        {
            FileStreamFileSystem fileSystem = new FileStreamFileSystem("files.bin");
            while (true)
            {
                Console.Clear();
                Console.WriteLine("Write h to see all commands.");

                int currentDirectoryOffset = fileSystem.CurrentDirectoryOffset;
                Console.WriteLine($"{fileSystem.GetDirectoryNode(currentDirectoryOffset).Name}");
                string[] inputs = Split(Console.ReadLine());
                if (inputs.Length == 0)
                {
                    Console.WriteLine($"Please add a command.");
                }
                else
                {


                    string command = inputs[0];
                    switch (command)
                    {
                        case "cpin":
                            if (inputs.Length != 3)
                            {
                                Console.WriteLine($"Please provide data in the correct format.");
                                Console.ReadLine();
                                break;
                            }
                            string originalFilePath = inputs[1];
                            string newFileName = inputs[2];
                            if (!File.Exists(originalFilePath))
                            {
                                throw new FileNotFoundException();
                            }
                            FileBocksLinkedList fileBocks = new FileBocksLinkedList();
                            int blocks_count = 0;
                            FileStream fs = new FileStream(originalFilePath, FileMode.Open, FileAccess.Read);
                            BinaryReader br = new BinaryReader(fs);
                            byte[] blockContent;
                            while (fs.Position < fs.Length)
                            {
                                blockContent = new byte[FileStreamFileSystem.FILE_BLOCK_SIZE];
                                br.Read(blockContent, 0, FileStreamFileSystem.FILE_BLOCK_SIZE);
                                fileBocks.Append(blockContent);
                                blocks_count++;
                            }
                            br.Close();
                            fs.Close();
                            MyFile newFile = new MyFile();
                            newFile.Name = newFileName;
                            newFile.FileBlocks = fileBocks;
                            newFile.BlocksCount = blocks_count;
                            fileSystem.AddFile(newFile);
                            Console.ReadLine();
                            break;

                        case "ls":
                            {
                                if (inputs.Length != 1)
                                {
                                    Console.WriteLine($"Please provide data in the correct format.");
                                    Console.ReadLine();
                                    break;
                                }
                                Console.WriteLine("Files:");
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset('f');
                                FileNode current = null;
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetFileNode(currentOffset);
                                    if (!current.IsDeleted)
                                    {
                                        Console.WriteLine($"{current.Value.Name}    {current.Value.BlocksCount * FileStreamFileSystem.FILE_BLOCK_SIZE}B");
                                    }
                                    currentOffset = current.NextOffset;
                                }
                                Console.WriteLine("Folders:");
                                currentOffset = fileSystem.GetFirstElementInDirectoryOffset('d');
                                DirectoryNode directory = null;
                                while (currentOffset != -1)
                                {
                                    directory = fileSystem.GetDirectoryNode(currentOffset);
                                    if (!directory.IsDeleted)
                                    {
                                        Console.WriteLine($"{directory.Name}    {directory.Size}B");
                                    }
                                    currentOffset = directory.NextOffset;
                                }
                                Console.ReadLine();
                            }
                            break;

                        case "rm":
                            {
                                if (inputs.Length != 2)
                                {
                                    Console.WriteLine($"Please provide data in the correct format.");
                                    Console.ReadLine();
                                    break;
                                }
                                string fileToDelete = inputs[1];
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset('f');
                                FileNode current = null;
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetFileNode(currentOffset);
                                    if (!current.IsDeleted && current.Value.Name == fileToDelete)
                                    {
                                        fileSystem.RemoveFileNode(currentOffset);
                                        Console.WriteLine("Successful removal.");
                                        break;
                                    }
                                    currentOffset = current.NextOffset;
                                }
                                if (currentOffset == -1)
                                {
                                    Console.WriteLine("No such file found.");
                                }
                                Console.ReadLine();
                            }
                            break;
                        case "cpout":
                            {
                                if (inputs.Length != 3)
                                {
                                    Console.WriteLine($"Please provide data in the correct format.");
                                    Console.ReadLine();
                                    break;
                                }
                                string fileToCopy = inputs[1];
                                string newFilePath = inputs[2];
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset('f');
                                FileNode current;
                                fs = new FileStream(newFilePath, FileMode.Create, FileAccess.Write);
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetFileNode(currentOffset);
                                    if (!current.IsDeleted && current.Value.Name == fileToCopy)
                                    {
                                        BlockNode block = current.Value.FileBlocks.FirstBlockNode;
                                        while (block != null)
                                        {
                                            fs.Write(block.Content);
                                            block = block.Next;
                                        }
                                        Console.WriteLine("File copied successfully.");
                                        break;
                                    }
                                    currentOffset = current.NextOffset;
                                }
                                fs.Close();
                                if (currentOffset == -1)
                                {
                                    Console.WriteLine("No such file found.");
                                }
                                Console.ReadLine();
                                break;
                            }
                        case "md":
                            if (inputs.Length != 2)
                            {
                                Console.WriteLine($"Please provide data in the correct format.");
                                Console.ReadLine();
                                break;
                            }
                            string directoryName = inputs[1];
                            fileSystem.MakeDirectory(directoryName);
                            Console.ReadLine();
                            break;
                        case "cd":
                            if (inputs.Length != 2)
                            {
                                Console.WriteLine($"Please provide data in the correct format.");
                                Console.ReadLine();
                                break;
                            }
                            directoryName = inputs[1];
                            fileSystem.ChangeDirectory(directoryName);
                            Console.ReadLine();
                            break;
                        case "rd":
                            {
                                if (inputs.Length != 2)
                                {
                                    Console.WriteLine($"Please provide data in the correct format.");
                                    Console.ReadLine();
                                    break;
                                }
                                directoryName = inputs[1];
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset('d');
                                DirectoryNode current;
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetDirectoryNode(currentOffset);
                                    if (!current.IsDeleted && current.Name == directoryName)
                                    {
                                        fileSystem.RemoveDirectory(currentOffset);
                                        break;
                                    }
                                    currentOffset = current.NextOffset;
                                }
                                if (currentOffset == -1)
                                {
                                    Console.WriteLine($"No such folder in the current directory.");
                                }
                                Console.ReadLine();
                                break;
                            }
                        case "h":
                            Console.WriteLine("cpin c:\\aaa.txt bbb.txt - copy the file aaa.txt to the container with the name bbb.txt");
                            Console.WriteLine("ls - list all files in the current directory of the container.");
                            Console.WriteLine("rm bbb.txt - remove the file bbb.txt from the current directory of the container.");
                            Console.WriteLine("cpout bbb.txt c:\\ttt.txt  - copy the file bbb.txt from the current directory of the container to the specified path c:\\ttt.txt.");
                            Console.WriteLine("md FolderAAA - make new directory named FolderAAA in the current directory of the container.");
                            Console.WriteLine("cd FolderAAA - change the current directory of the container to its child directory FolderAAA.");
                            Console.WriteLine("cd .. - change the current directory of the container to its parent one.");
                            Console.WriteLine("cd \\ - change the current directory of the container to the main one.");
                            Console.WriteLine("rd FolderAAA - remove the directory named FolderAAA from the current directory of the container.");
                            Console.WriteLine("q - quit.");
                            Console.ReadLine();
                            break;
                        case "q":
                            fileSystem.Dispose();
                            return;
                    }
                }
            }
        }
        static string[] Split(string input)
        {
            WordsLinkedList words = new WordsLinkedList();
            string currentWord = "";
            foreach (char c in input)
            {
                if (c == ' ')
                {
                    if (currentWord.Length > 0)
                    {
                        words.Append(currentWord);
                        currentWord = "";
                    }
                }
                else
                {
                    currentWord += c;
                }
            }
            if (currentWord.Length > 0)
            {
                words.Append(currentWord);
            }
            string[] result = new string[words.Count];
            WordNode current = words.FirstWordNode;
            int index = 0;
            while (current != null)
            {
                result[index++] = current.Word;
                current = current.Next;
            }
            return result;
        }
    }
    public class WordNode
    {
        public string Word;
        public WordNode Next;
    }
    public class WordsLinkedList
    {
        public WordNode FirstWordNode;
        public int Count = 0;
        public void Append(string word)
        {
            WordNode newNode = new WordNode();
            newNode.Word = word;
            Count++;
            if (FirstWordNode == null)
                FirstWordNode = newNode;
            else
            {
                WordNode current = FirstWordNode;
                WordNode last = null;
                while (current != null)
                {
                    last = current;
                    current = current.Next;
                }
                last.Next = newNode;
            }
        }
    }
}

