namespace MFTLib
{
    /// <summary>
    /// Node inside the tree structure representing the MFT file system
    /// </summary>
    public class MFTNode
    {
        public readonly Guid Guid;
        public string FileName;
        public long Size;
        
        Dictionary<Guid, MFTNode> _children = new();
        
        internal MFTNode()
        {
        }

        internal MFTNode(Guid guid, MFTLibFile mftLibFile)
        {
            Guid = guid;
            FileName = mftLibFile.FileName;
            Size = mftLibFile.Size;
        }

        public void AddFile(MFTNode mftNode)
        {
            _children.Add(mftNode.Guid, mftNode);
        }

        public void Print(int indent = 0)
        {
            var indentString = new string(' ', indent * 2);
            Console.WriteLine($"Node {Guid}: =================================");
            Console.WriteLine($"FileName: {FileName}");
            Console.WriteLine($"Size: {Size}");
            Console.WriteLine($"Children ({_children.Count}:");
            foreach (var kvp in _children)
            {
                kvp.Value.Print(indent + 1);
            }
            Console.WriteLine($"End MFTNode {Guid} ===========================");
        }
    }
}
