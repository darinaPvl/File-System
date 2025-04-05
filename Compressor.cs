namespace File_System.Compressor
{
    public class BoolStack
    {
        Node _top;
        public int Count = 0;
        private class Node
        {
            public bool Value;
            public Node Next;
        }
        public void Push(bool value)
        {
            Node newNode = new Node();
            newNode.Value = value;
            newNode.Next = _top;
            _top = newNode;
            Count++;
        }
        public bool Pop()
        {
            if (_top == null) throw new InvalidOperationException("Stack is empty");
            bool value = _top.Value;
            _top = _top.Next;
            Count--;
            return value;
        }
        public byte[] ToByteArray()
        {
            byte[] bytes = new byte[(int)Math.Ceiling(Count / 8.0)];
            Node current = _top;
            for (int i = 0; i < Count; i++)
            {
                if (current.Value)
                {
                    bytes[i / 8] |= (byte)(1 << (7 - i % 8));
                }
                current = current.Next;
            }
            return bytes;
        }
        public void Print()
        {
            Node current = _top;
            for (int i = 0; i < Count; i++)
            {
                Console.Write(current.Value ? 1 : 0);
                current = current.Next;
            }
        }
    }
    public class ByteStack
    {
        Node _top;
        public int Count = 0;
        private class Node
        {
            public byte Value;
            public Node Next;
        }
        public void Push(byte value)
        {
            Node newNode = new Node();
            newNode.Value = value;
            newNode.Next = _top;
            _top = newNode;
            Count++;
        }
        public byte Pop()
        {
            if (_top == null) throw new InvalidOperationException("Stack is empty");
            byte value = _top.Value;
            _top = _top.Next;
            Count--;
            return value;
        }
        public byte[] ToByteArray()
        {
            byte[] arr = new byte[Count];
            Node current = _top;
            for (int i = 0; i < Count; i++)
            {
                arr[i] = current.Value;
                current = current.Next;
            }
            return arr;
        }
    }
    public class HuffmanNode
    {
        public byte Byte;
        public int Freq;
        public bool IsLeaf;
        public HuffmanNode Left;
        public HuffmanNode Right;
        public HuffmanNode Next;
    }
    class HuffmanCompressor
    {
        public const int BYTE_VALUES = 256;
        public class HuffmanLinkedList
        {
            public HuffmanNode First;
            public int Count;
            public void Append(HuffmanNode newNode)
            {
                HuffmanNode current = First;
                if (First == null || First.Freq >= newNode.Freq)
                {
                    First = newNode;
                    newNode.Next = current;
                }
                else
                {
                    HuffmanNode last = null;
                    while (current != null)
                    {
                        if (current.Freq >= newNode.Freq)
                        {
                            newNode.Next = current;
                            break;
                        }
                        last = current;
                        current = current.Next;
                    }
                    last.Next = newNode;
                }
                Count++;
            }
            public HuffmanNode PopFirst()
            {
                HuffmanNode node = First;
                First = First?.Next;
                Count--;
                return node;
            }
        }

        public HuffmanNode BuildTree(byte[] content)
        {
            int[] bytesFreqs = new int[BYTE_VALUES];

            for (int c = 0; c < content.Length; c++)
            {
                bytesFreqs[content[c]]++;
            }

            HuffmanLinkedList hll = new HuffmanLinkedList();
            for (int i = 0; i < bytesFreqs.Length; i++)
            {
                if (bytesFreqs[i] > 0)
                {
                    hll.Append(new HuffmanNode { Byte = (byte)i, Freq = bytesFreqs[i], IsLeaf = true });
                }
            }
            while (true)
            {
                HuffmanNode left = hll.PopFirst();
                HuffmanNode right = hll.PopFirst();

                HuffmanNode node = new HuffmanNode
                {
                    Left = left,
                    Right = right,
                    Freq = left.Freq + right.Freq
                };

                if (hll.First == null)
                {
                    return node;
                }
                else
                {
                    hll.Append(node);
                }
            }
        }

        // GetTreeHeight and PrintTree methods are for testing
        public int GetTreeHeight(HuffmanNode node)
        {
            if (node == null)
            {
                return 0;
            }
            int leftHeight = GetTreeHeight(node.Left);
            int rightHeight = GetTreeHeight(node.Right);
            if (leftHeight > rightHeight)
            {
                return 1 + leftHeight;
            }
            return 1 + rightHeight;
        }

        public void PrintTree(HuffmanNode[] levelNodes, int level, int treeHeight)
        {
            if (level == treeHeight)
            {
                return;
            }
            HuffmanNode[] nextLevel = new HuffmanNode[(int)Math.Pow(2, level + 1)];
            int nextIndex = 0;

            int spacesCount = (int)Math.Pow(2, treeHeight - level) - 1;
            foreach (HuffmanNode node in levelNodes)
            {
                for (int i = 0; i < spacesCount; i++)
                {
                    Console.Write(' ');
                }
                if (node == null)
                {
                    Console.Write(' ');
                    nextIndex += 2;
                }
                else
                {
                    if (node.IsLeaf)
                    {
                        Console.Write(node.Byte);
                    }
                    else
                    {
                        Console.Write(node.Freq);
                    }
                    nextLevel[nextIndex++] = node.Left;
                    nextLevel[nextIndex++] = node.Right;
                }
                for (int i = 0; i < spacesCount + 1; i++)
                {
                    Console.Write(' ');
                }
            }
            Console.WriteLine();
            PrintTree(nextLevel, level + 1, treeHeight);
        }

        public bool Encode(HuffmanNode node, byte b, BoolStack bools)
        {
            if (node == null)
            {
                return false;
            }

            if (node.IsLeaf && node.Byte == b)
            {
                return true;
            }

            if (Encode(node.Left, b, bools))
            {
                bools.Push(false);
                return true;
            }

            if (Encode(node.Right, b, bools))
            {
                bools.Push(true);
                return true;
            }
            return false;
        }

        public byte[] HuffmanEncode(HuffmanNode root, byte[] content)
        {
            BoolStack bools = new BoolStack();
            for (int c = 0; c < content.Length; c++)
            {
                Encode(root, content[c], bools);
            }
            return bools.ToByteArray();
        }

        public byte[] HuffmanDecode(HuffmanNode root, byte[] bytes, int contentLength)
        {
            BoolStack bools = new BoolStack();
            for (int i = bytes.Length - 1; i >= 0; i--)
            {
                for (int j = 0; j < 8; j++)
                {
                    bools.Push((bytes[i] & (1 << j)) != 0);
                }
            }
            // bools contains the encoded content backwards.
            byte[] content = new byte[contentLength];
            int contentIndex = contentLength - 1;
            HuffmanNode node = root;
            bool path;
            while (contentIndex >= 0 && bools.Count > 0)
            {
                path = bools.Pop();
                if (!node.IsLeaf)
                {
                    node = path ? node.Right : node.Left;
                }
                else
                {
                    content[contentIndex] = node.Byte;
                    contentIndex--;
                    node = path ? root.Right : root.Left;
                }
            }
            if (node.IsLeaf)
            {
                content[contentIndex] = node.Byte;
            }
            return content;
        }

        public void TreeToBytes(HuffmanNode node, ByteStack bytes)
        {
            if (node == null)
            {
                return;
            }
            bytes.Push(node.Byte);
            bytes.Push(node.IsLeaf ? (byte)1 : (byte)0);  // Flag: 1 - leaf, 0 - node
            TreeToBytes(node.Right, bytes);
            TreeToBytes(node.Left, bytes);
        }

        public HuffmanNode BytesToTree(byte[] bytes)
        {
            HuffmanLinkedList hll = new HuffmanLinkedList();
            for (int i = 0; i < bytes.Length; i += 2)
            {
                if (bytes[i] == 0) // node
                {
                    HuffmanNode newNode = new HuffmanNode
                    {
                        Right = hll.PopFirst(),
                        Left = hll.PopFirst()
                    };
                    hll.Append(newNode); // Since the freq is 0 hll acts as a stack
                }
                else
                {
                    hll.Append(new HuffmanNode { Byte = bytes[i + 1], IsLeaf = true });
                }
            }
            if (hll.Count == 1)
            {
                return hll.PopFirst();
            }
            return null;
        }
    }
}