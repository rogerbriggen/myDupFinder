namespace RogerBriggen.MyDupFinderLib
{
    public class MyDupFinderFindDupsJobDTO
    {
        public enum EFindDupsMode
        {
            FindOnlyDups,  //finds only duplicate files
            FindDupsTheWholeLot    //reports everything: duplicates, missing files, new files, changed files
        }

        public string JobName { get; set; }

        public string DatabaseFileBase { get; set; }

        public string DatabaseFile { get; set; }

        public EFindDupsMode FindDupsMode { get; set; }

        public string? ReportName { get; set; }


    }
}
