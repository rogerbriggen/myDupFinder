namespace RogerBriggen.MyDupFinderLib
{
    public class MyDupFinderScanJobDTO
    {
        public string JobName { get; set; }

        public string BasePath { get; set; }

        public string OriginComputer { get; set; }

        public string DatabaseFile { get; set; }

        public string? ReportName { get; set; }
    }
}
