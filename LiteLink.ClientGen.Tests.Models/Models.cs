namespace LiteLink.ClientGen.Tests.Models
{
    public class CgModel
    {
        public int? Id { get; set; }
        public string Name { get; set; }
        public double Value { get; set; }
        public CgEnum Kind { get; set; }
        public List<int> Numbers { get; set; } = new List<int>();
        public List<CgChild> Children { get; set; } = new List<CgChild>();
        public Dictionary<int, CgChild> ChildMap { get; set; } = new Dictionary<int, CgChild>();
        public (int, string) Pair { get; set; }
        public Guid Uid { get; set; }
        public DateTime When { get; set; }
        public decimal Price { get; set; }
        public byte[] Blob { get; set; }
        public CgEnum? MaybeKind { get; set; }
    }

    public class CgChild
    {
        public int Id { get; set; }
        public string Name { get; set; }
        //self-reference exercises the cycle handling in fingerprints and plans
        public CgChild Next { get; set; }
    }

    /// <summary>non-int underlying type so fingerprints must carry the real width</summary>
    public enum CgEnum : short
    {
        Alpha = 0,
        Beta = 1,
    }

    /// <summary>only ever sent via the dynamic proxy, so it must be listed on [RpcClientModels]</summary>
    public class CgListedOnly
    {
        public string Tag { get; set; }
    }
}
