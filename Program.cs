
using System.Collections.Generic;
using File_System.Compressor;

namespace File_System
{
    public enum ContainerElementTypeEnum
    {
        File,
        Folder
    }
    class DirectoryNode
    {
        public int Offset;
        public int Size;
        public string Name;
        public int ParentDirectoryOffset;
        public int PreviousDirectoryInDirectoryOffset;
        public int NextDirectoryInDirectoryOffset;
        public int FirstDirectoryOffset;
        public int FirstFileOffset;
    }
    class FileNode
    {
        public int Offset;
        public int BlocksCount;
        public string Name;
        public int ParentDirectoryOffset;
        public int PreviousFileInDirectoryOffset;
        public int NextFileInDirectoryOffset;
        public int PositionsForFileContent;
    }
    class FileStreamFileSystem
    {
        // | DeletesCount (4B) |
        // | ParentDirectoryOffset (4B)|
        // if directory:
        //      | PreviousDirectoryInDirectoryOffset (4B) | NextDirectoryInDirectoryOffset (4B) | Size (4B) |
        //      | FirstDirectoryOffset (4B) | FirstFileOffset (4B) | Name | 
        // if file:
        //      | PreviousFileInDirectoryOffset (4B) | NextFileInDirectoryOffset (4B) | BlocksCount (4B) |
        //      | PositionsForFileContent (4B) | Data - Blocks Content | Name | 

        public const int FILE_BLOCK_SIZE = 1024; // 1KB
        public const int DELETES_COUNT_FOR_DEFRAGMENTATION = 5;
        uint CRC_DIVISOR = 0x04C11DB7;

        public int FirstOffset;
        public int CurrentDirectoryOffset;

        private FileStream _fs;
        private BinaryReader _br;
        private BinaryWriter _bw;
        private string _containerPath;
        private HuffmanCompressor _compressor;

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
            _compressor = new HuffmanCompressor();

            if (_fs.Length == 0)
            {
                DeletesCount = 0;
                _bw.Write(DeletesCount);
                // The main directory
                _bw.Write(-1); // ParentDirectoryOffset
                _bw.Write(-1); // PreviousDirectoryInDirectoryOffset
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
        public void AddFileBlock(byte[] content)
        {
            // | CRC | content.Length | treeBytes.Length | treeBytes | encodedBytes.Length | encodedBytes |
            _fs.Position = _fs.Length;
            _bw.Write(GetCRC(content));
            _bw.Write(content.Length);
            HuffmanNode root = _compressor.BuildTree(content);
            ByteStack bytes = new ByteStack();
            _compressor.TreeToBytes(root, bytes);
            byte[] treeBytes = bytes.ToByteArray();
            _bw.Write(treeBytes.Length);
            _bw.Write(treeBytes);
            byte[] encodedBytes = _compressor.HuffmanEncode(root, content);
            _bw.Write(encodedBytes.Length);
            _bw.Write(encodedBytes);
        }
        public void AddFile(string originalFilePath, string newFileName)
        {
            // | ParentDirectoryOffset (4B) | PreviousFileInDirectoryOffset (4B) | NextFileInDirectoryOffset (4B) |
            // | BlocksCount (4B) | PositionsForFileContent (4B) | Data - Blocks Content | Name |

            if (!File.Exists(originalFilePath))
            {
                throw new FileNotFoundException();
            }
            int newFileOffset = (int)_fs.Length;
            int lastFileInDirectoryOffset = GetLastElementInDirectoryOffset(ContainerElementTypeEnum.File);
            _fs.Position = newFileOffset;
            _bw.Write(CurrentDirectoryOffset); // ParentDirectoryOffset
            _bw.Write(lastFileInDirectoryOffset); // PreviousFileInDirectoryOffset
            _bw.Write(-1); // NextFileInDirectoryOffset
            _bw.Write(0); // BlocksCount - will be updated
            _bw.Write(0); // PositionsForFileContent - will be updated
            int blocks_count = 0;
            int startPosition = (int)_fs.Position;
            FileStream fs = new FileStream(originalFilePath, FileMode.Open, FileAccess.Read);
            BinaryReader br = new BinaryReader(fs);
            byte[] blockContent = new byte[FILE_BLOCK_SIZE];
            int bytesRead;
            while (fs.Position < fs.Length)
            {
                bytesRead = br.Read(blockContent, 0, FILE_BLOCK_SIZE);
                if (bytesRead < FILE_BLOCK_SIZE)
                {
                    byte[] block = new byte[bytesRead];
                    Array.Copy(blockContent, block, bytesRead);
                    blockContent = block;
                }
                AddFileBlock(blockContent);
                blocks_count++;
            }
            br.Close();
            fs.Close();
            int positionsForFileContent = (int)_fs.Position - startPosition;

            _bw.Write(newFileName); // Name
            _fs.Position = newFileOffset + 3 * sizeof(int);
            _bw.Write(blocks_count); // BlocksCount update
            _bw.Write(positionsForFileContent); // PositionsForFileContent update

            if (lastFileInDirectoryOffset != -1)
            {
                _fs.Position = lastFileInDirectoryOffset + 2 * sizeof(int);
                _bw.Write(newFileOffset); // NextFileInDirectoryOffset for the parent directory
            }
            else
            {
                _fs.Position = CurrentDirectoryOffset + 5 * sizeof(int);
                _bw.Write(newFileOffset); // FirstFileOffset for the parent directory
            }
            int parentDirectoryOffset = CurrentDirectoryOffset;
            while (parentDirectoryOffset != -1)
            {
                UpdateDirectorySize(parentDirectoryOffset, blocks_count * FILE_BLOCK_SIZE);
                _fs.Position = parentDirectoryOffset;
                parentDirectoryOffset = _br.ReadInt32(); // ParentDirectoryOffset
            }
            _fs.Flush();
        }
        public DirectoryNode GetDirectoryNode(int nodeOffset)
        {
            DirectoryNode result = new DirectoryNode();
            result.Offset = nodeOffset;
            _fs.Position = nodeOffset;
            result.ParentDirectoryOffset = _br.ReadInt32();
            result.PreviousDirectoryInDirectoryOffset = _br.ReadInt32();
            result.NextDirectoryInDirectoryOffset = _br.ReadInt32();
            result.Size = _br.ReadInt32();
            result.FirstDirectoryOffset = _br.ReadInt32();
            result.FirstFileOffset = _br.ReadInt32();
            result.Name = _br.ReadString();
            return result;
        }
        public FileNode GetFileNode(int nodeOffset)
        {
            FileNode result = new FileNode();
            result.Offset = nodeOffset;
            _fs.Position = nodeOffset;
            result.ParentDirectoryOffset = _br.ReadInt32();
            result.PreviousFileInDirectoryOffset = _br.ReadInt32();
            result.NextFileInDirectoryOffset = _br.ReadInt32();
            result.BlocksCount = _br.ReadInt32();
            result.PositionsForFileContent = _br.ReadInt32();
            _fs.Position += result.PositionsForFileContent;
            result.Name = _br.ReadString();
            return result;
        }
        public (byte[], int) GetValidatedFileBlock(int offset)
        {
            (byte[] fileBlock, int nextOffset) = GetFileBlock(offset);
            if (GetBlockCRC(offset) != GetCRC(fileBlock))
            {
                throw new FileLoadException();
            }
            return (fileBlock, nextOffset);
        }
        public int GetBlockCRC(int offset)
        {
            _fs.Position = offset;
            return _br.ReadInt32();
        }
        public (byte[], int) GetFileBlock(int offset)
        {
            // Returns the fileBlockContent and the next block's start offset
            _fs.Position = offset + sizeof(int);
            int contentLength = _br.ReadInt32();
            int treeBytesLength = _br.ReadInt32();
            byte[] treeBytes = new byte[treeBytesLength];
            _br.Read(treeBytes, 0, treeBytesLength);
            HuffmanNode root = _compressor.BytesToTree(treeBytes);
            ByteStack bytes = new ByteStack();
            int encodedBytesLength = _br.ReadInt32();
            byte[] encodedBytes = new byte[encodedBytesLength];
            _br.Read(encodedBytes, 0, encodedBytesLength);
            return (_compressor.HuffmanDecode(root, encodedBytes, contentLength), (int)_fs.Position);
        }

        // For testing purposes
        public void CorruptFileBlock(int offset)
        {
            _fs.Position = offset + 2 * sizeof(int);
            int treeBytesLength = _br.ReadInt32();
            _fs.Position += treeBytesLength;
            int encodedBytesLength = _br.ReadInt32();
            _bw.Write(new byte[encodedBytesLength]);
        }
        public void RemoveFileNode(FileNode fileNode)
        {
            if (fileNode.PreviousFileInDirectoryOffset != -1)
            {
                _fs.Position = fileNode.PreviousFileInDirectoryOffset + 2 * sizeof(int);
                _bw.Write(fileNode.NextFileInDirectoryOffset); // NextFileInDirectoryOffset
            }
            else
            {
                _fs.Position = fileNode.ParentDirectoryOffset + 5 * sizeof(int);
                _bw.Write(fileNode.NextFileInDirectoryOffset); // FirstFileOffset
            }
            if (fileNode.NextFileInDirectoryOffset != -1)
            {
                _fs.Position = fileNode.NextFileInDirectoryOffset + sizeof(int);
                _bw.Write(fileNode.PreviousFileInDirectoryOffset); // PreviousFileInDirectoryOffset
            }

            int parentDirectoryOffset = fileNode.ParentDirectoryOffset;
            while (parentDirectoryOffset != -1)
            {
                UpdateDirectorySize(parentDirectoryOffset, -fileNode.BlocksCount * FILE_BLOCK_SIZE);
                _fs.Position = parentDirectoryOffset;
                parentDirectoryOffset = _br.ReadInt32(); // ParentDirectoryOffset
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
            int newDirectoryOffset = (int)_fs.Length;
            int lastDirectoryInDirectoryOffset = GetLastElementInDirectoryOffset(ContainerElementTypeEnum.Folder);
            _fs.Position = newDirectoryOffset;
            _bw.Write(CurrentDirectoryOffset); // ParentDirectoryOffset
            _bw.Write(lastDirectoryInDirectoryOffset); // PreviousDirectoryInDirectoryOffset
            _bw.Write(-1); // NextDirectoryInDirectoryOffset
            _bw.Write(0); // Size
            _bw.Write(-1); // FirstDirectoryOffset
            _bw.Write(-1); // FirstFileOffset
            _bw.Write(newDirectoryName); // Name

            if (lastDirectoryInDirectoryOffset != -1)
            {
                _fs.Position = lastDirectoryInDirectoryOffset + 2 * sizeof(int);
                _bw.Write(newDirectoryOffset); // NextDirectoryInDirectoryOffset for the parent directory
            }
            else
            {
                _fs.Position = CurrentDirectoryOffset + 4 * sizeof(int);
                _bw.Write(newDirectoryOffset); // FirstDirectoryOffset for the parent directory
            }

            _fs.Flush();
        }
        public int GetFirstElementInDirectoryOffset(ContainerElementTypeEnum cet)
        {
            if (cet == ContainerElementTypeEnum.Folder)
            {
                _fs.Position = CurrentDirectoryOffset + 4 * sizeof(int);
            }
            else
            {
                _fs.Position = CurrentDirectoryOffset + 5 * sizeof(int);
            }
            return _br.ReadInt32();
        }
        public int GetLastElementInDirectoryOffset(ContainerElementTypeEnum cet)
        {
            int currentElementOffset = GetFirstElementInDirectoryOffset(cet);
            int lastElementOffset = currentElementOffset;
            while (currentElementOffset != -1)
            {
                lastElementOffset = currentElementOffset;
                _fs.Position = currentElementOffset + 2 * sizeof(int);
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
                            _fs.Position = CurrentDirectoryOffset;
                            CurrentDirectoryOffset = _br.ReadInt32();
                        }
                        break;
                    }
                default:
                    {
                        int currentOffset = GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.Folder);
                        DirectoryNode current;
                        while (currentOffset != -1)
                        {
                            current = GetDirectoryNode(currentOffset);
                            if (current.Name == directoryName)
                            {
                                CurrentDirectoryOffset = currentOffset;
                                break;
                            }
                            currentOffset = current.NextDirectoryInDirectoryOffset;
                        }
                        if (currentOffset == -1)
                        {
                            Console.WriteLine($"No such folder in the current directory.");
                        }
                        return;
                    }
            }

        }
        public void RemoveDirectory(DirectoryNode directoryNode)
        {
            if (directoryNode.PreviousDirectoryInDirectoryOffset != -1)
            {
                _fs.Position = directoryNode.PreviousDirectoryInDirectoryOffset + 2 * sizeof(int);
                _bw.Write(directoryNode.NextDirectoryInDirectoryOffset); // NextDirectoryInDirectoryOffset
            }
            else
            {
                _fs.Position = directoryNode.ParentDirectoryOffset + 4 * sizeof(int);
                _bw.Write(directoryNode.NextDirectoryInDirectoryOffset); // FirstDirectoryOffset
            }
            if (directoryNode.NextDirectoryInDirectoryOffset != -1)
            {
                _fs.Position = directoryNode.NextDirectoryInDirectoryOffset + sizeof(int);
                _bw.Write(directoryNode.PreviousDirectoryInDirectoryOffset); // PreviousDirectoryInDirectoryOffset
            }
            int parentDirectoryOffset = directoryNode.ParentDirectoryOffset;
            while (parentDirectoryOffset != -1)
            {
                UpdateDirectorySize(parentDirectoryOffset, -directoryNode.Size);
                _fs.Position = parentDirectoryOffset;
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
            _fs.Position = directoryOffset + 3 * sizeof(int);
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
            tempBW.Write(0); // DeletesCount
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
            int lastOffset = -1;
            if (newParentDirectoryOffset != -1)
            {
                tempFS.Position = newParentDirectoryOffset + 4 * sizeof(int);
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
                        lastOffset = currentOffset;
                        tempFS.Position = currentOffset + 2 * sizeof(int);
                        currentOffset = tempBR.ReadInt32(); // NextDirectoryInDirectoryOffset
                    }
                    tempFS.Position -= sizeof(int);
                    tempBW.Write(newDirectoryOffset);
                }
            }
            tempFS.Position = newDirectoryOffset;
            tempBW.Write(newParentDirectoryOffset);
            tempBW.Write(lastOffset); // PreviousDirectoryInDirectoryOffset
            tempBW.Write(-1); // NextDirectoryInDirectoryOffset
            tempBW.Write(oldDirectory.Size);
            tempBW.Write(-1); // FirstDirectoryOffset
            tempBW.Write(-1); // FirstFileOffset
            tempBW.Write(oldDirectory.Name);

            int lastFileOffset = -1;
            int newFileOffset;
            int newFirstFileOffset = -1;
            CurrentDirectoryOffset = oldDirectoryOffset;

            // add the files from the directory
            int currentFileOffset = GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.File);
            FileNode currentFile;
            while (currentFileOffset != -1)
            {
                currentFile = GetFileNode(currentFileOffset);
                newFileOffset = (int)tempFS.Length;
                if (newFirstFileOffset == -1)
                {
                    newFirstFileOffset = newFileOffset;
                }
                else
                {
                    // The last element in the new container file is a file from the directory we are adding.
                    tempFS.Position = lastFileOffset + 2 * sizeof(int);
                    tempBW.Write(newFileOffset); // NextFileInDirectoryOffset
                }
                tempFS.Position = newFileOffset;
                tempBW.Write(newDirectoryOffset); // ParentDirectoryOffset
                tempBW.Write(lastFileOffset); // PreviousFileInDirectoryOffset
                tempBW.Write(-1); // NextFileInDirectoryOffset
                tempBW.Write(currentFile.BlocksCount); // BlocksCount
                tempBW.Write(currentFile.PositionsForFileContent); // PositionsForFileContent
                tempBW.Write(GetBytesInInterval(currentFileOffset + 5 * sizeof(int), currentFile.PositionsForFileContent));
                tempBW.Write(currentFile.Name); // Name

                currentFileOffset = currentFile.NextFileInDirectoryOffset;
                lastFileOffset = newFileOffset;
            }
            tempFS.Position = newDirectoryOffset + 5 * sizeof(int);
            tempBW.Write(newFirstFileOffset);

            // add the directories from the directory
            int currentDirectoryOffset = GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.Folder);
            DirectoryNode currentDirectory;
            while (currentDirectoryOffset != -1)
            {
                currentDirectory = GetDirectoryNode(currentDirectoryOffset);
                AddDefragmentedDirectory(currentDirectoryOffset, newDirectoryOffset);
                currentDirectoryOffset = currentDirectory.NextDirectoryInDirectoryOffset;
            }
        }
        public byte[] GetBytesInInterval(int fromOffset, int count)
        {
            byte[] bytes = new byte[count];
            _fs.Position = fromOffset;
            _br.Read(bytes, 0, bytes.Length);
            return bytes;
        }
        public int GetCRC(byte[] data)
        {
            int dataLength = data.Length * 8;
            int divisorLength = GetBitLength(CRC_DIVISOR);
            int totalLength = dataLength + divisorLength - 1;

            int[] tempData = new int[totalLength];
            for (int i = 0; i < data.Length; i++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    tempData[i * 8 + bit] = (data[i] >> (7 - bit)) & 1;
                }
            }
            for (int i = 0; i < dataLength; i++)
            {
                if (tempData[i] == 1)
                {
                    for (int j = 0; j < divisorLength; j++)
                    {
                        tempData[i + j] ^= (int)((CRC_DIVISOR >> (divisorLength - 1 - j)) & 1);
                    }
                }
            }
            int remainder = 0;
            for (int i = 0; i < divisorLength - 1; i++)
            {
                remainder |= (int)(tempData[dataLength + i] << ((divisorLength - 2) - i));
            }
            return remainder;
        }
        static int GetBitLength(uint value)
        {
            int length = 0;
            while (value > 0)
            {
                length++;
                value >>= 1;
            }
            return length;
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
                    Console.WriteLine($"Please enter a valid command.");
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
                            try
                            {
                                fileSystem.AddFile(originalFilePath, newFileName);
                            }
                            catch (FileNotFoundException)
                            {
                                Console.WriteLine($"Please provide an existing file.");
                            }
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
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.File);
                                FileNode current;
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetFileNode(currentOffset);
                                    Console.WriteLine($"{current.Name}    {current.BlocksCount * FileStreamFileSystem.FILE_BLOCK_SIZE}B");
                                    currentOffset = current.NextFileInDirectoryOffset;
                                }
                                Console.WriteLine("Folders:");
                                currentOffset = fileSystem.GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.Folder);
                                DirectoryNode directory;
                                while (currentOffset != -1)
                                {
                                    directory = fileSystem.GetDirectoryNode(currentOffset);
                                    Console.WriteLine($"{directory.Name}    {directory.Size}B");
                                    currentOffset = directory.NextDirectoryInDirectoryOffset;
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
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.File);
                                FileNode current;
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetFileNode(currentOffset);
                                    if (current.Name == fileToDelete)
                                    {
                                        fileSystem.RemoveFileNode(current);
                                        Console.WriteLine("Successful removal.");
                                        break;
                                    }
                                    currentOffset = current.NextFileInDirectoryOffset;
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
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.File);
                                FileNode current;
                                FileStream fs = new FileStream(newFilePath, FileMode.Create, FileAccess.Write);
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetFileNode(currentOffset);
                                    if (current.Name == fileToCopy)
                                    {
                                        try
                                        {
                                            byte[] block;
                                            int blockOffset = currentOffset + 5 * sizeof(int);
                                            for (int i = 0; i < current.BlocksCount; i++)
                                            {
                                                (block, blockOffset) = fileSystem.GetValidatedFileBlock(blockOffset);
                                                fs.Write(block);
                                            }
                                            Console.WriteLine("File copied successfully.");
                                            fs.Dispose();
                                            break;
                                        }
                                        catch (FileLoadException)
                                        {
                                            Console.WriteLine("The file is corrupted and can't be used anymore. Please remove it from the container.");
                                            fs.Dispose();
                                            File.Delete(newFilePath);
                                            break;
                                        }
                                    }
                                    currentOffset = current.NextFileInDirectoryOffset;
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
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.Folder);
                                DirectoryNode current;
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetDirectoryNode(currentOffset);
                                    if (current.Name == directoryName)
                                    {
                                        fileSystem.RemoveDirectory(current);
                                        break;
                                    }
                                    currentOffset = current.NextDirectoryInDirectoryOffset;
                                }
                                if (currentOffset == -1)
                                {
                                    Console.WriteLine($"No such folder in the current directory.");
                                }
                                Console.ReadLine();
                                break;
                            }
                        case "h":
                            {
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
                            }
                        // For testing
                        case "corrupt":
                            {
                                if (inputs.Length != 2)
                                {
                                    break;
                                }
                                string fileName = inputs[1];
                                int currentOffset = fileSystem.GetFirstElementInDirectoryOffset(ContainerElementTypeEnum.File);
                                FileNode current;
                                while (currentOffset != -1)
                                {
                                    current = fileSystem.GetFileNode(currentOffset);
                                    if (current.Name == fileName)
                                    {
                                        fileSystem.CorruptFileBlock(currentOffset + 5 * sizeof(int));
                                        break;
                                    }
                                    currentOffset = current.NextFileInDirectoryOffset;
                                }
                                break;
                            }
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