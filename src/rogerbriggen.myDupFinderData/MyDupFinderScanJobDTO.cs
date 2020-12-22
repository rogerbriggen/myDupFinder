namespace RogerBriggen.MyDupFinderData
{
    public class MyDupFinderScanJobDTO
    {
        public string JobName { get; set; } = string.Empty;

        public string BasePath { get; set; } = string.Empty;

        public string OriginComputer { get; set; } = string.Empty;

        public string ScanName { get; set; } = string.Empty;

        public string DatabaseFile { get; set; } = string.Empty;

        public string ReportName { get; set; } = string.Empty;


        public static void CheckSanity(MyDupFinderScanJobDTO dto)
        {
            //Rule: BasePath, DatabaseFile, scanName, OriginComputer and JobName may not be null
            if (string.IsNullOrWhiteSpace(dto.JobName))
            {
                throw new ParameterException("Param JobName may not be null or empty");
            }
            if (string.IsNullOrWhiteSpace(dto.OriginComputer))
            {
                throw new ParameterException("Param OriginComputer may not be null or empty");
            }
            if (string.IsNullOrWhiteSpace(dto.ScanName))
            {
                throw new ParameterException("Param ScanName may not be null or empty");
            }
            if (string.IsNullOrWhiteSpace(dto.BasePath))
            {
                throw new ParameterException("Param BasePath may not be null or empty");
            }
            if (string.IsNullOrWhiteSpace(dto.DatabaseFile))
            {
                throw new ParameterException("Param DatabaseFile may not be null or empty");
            }

            //Rule: BasePath must exist
            if (!System.IO.Directory.Exists(dto.BasePath))
            {
                throw new ParameterException($"BasePath must exist! {dto.BasePath}");
            }

            //Rule: DatabaseFile and ReportFile are not allowed below the BasePath
            string basePath = FileHelper.AddDirectoryDelimiter(dto.BasePath);
            if (dto.DatabaseFile.StartsWith(basePath))
            {
                throw new ParameterException("Param DatabaseFile may not be a subdirectory of BasePath!");
            }
            if (dto.ReportName.StartsWith(basePath))
            {
                throw new ParameterException("Param ReportName may not be a subdirectory of BasePath!");
            }
        }

        public static void FixDto(MyDupFinderScanJobDTO dto)
        {
            // Rule: Make sure the base path has a delimiter at the end
            dto.BasePath = FileHelper.AddDirectoryDelimiter(dto.BasePath);
        }
    }
}
