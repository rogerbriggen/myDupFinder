namespace RogerBriggen.MyDupFinderData
{
    public class MyDupFinderFindDupsJobDTO
    {
        public enum EFindDupsMode
        {
            FindOnlyDups,  //finds only duplicate files
            FindDupsTheWholeLot    //reports everything: duplicates, missing files, new files, changed files
        }

        public string JobName { get; set; } = string.Empty;

        public string DatabaseFileBase { get; set; } = string.Empty;

        public string DatabaseFile { get; set; } = string.Empty;

        public EFindDupsMode FindDupsMode { get; set; }

        public string ReportName { get; set; } = string.Empty;


    }
}
