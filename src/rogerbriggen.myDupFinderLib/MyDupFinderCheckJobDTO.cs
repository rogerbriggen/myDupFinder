namespace RogerBriggen.MyDupFinderLib
{
    public class MyDupFinderCheckJobDTO
    {
        public MyDupFinderScanJobDTO ScanJobDTO { get; set; }

        public bool IgnoreBasePath { get; set; }

        public bool SkipHashCheck { get; set; }

    }
}
