// Roger Briggen license this file to you under the MIT license.
//

namespace RogerBriggen.MyDupFinderData;

public class MyDupFinderCheckJobDTO
{
    public MyDupFinderCheckJobDTO() => ScanJobDTO = new MyDupFinderScanJobDTO();
    public MyDupFinderScanJobDTO ScanJobDTO { get; set; }

    /// <summary>
    /// When true, matches DB rows by the relative sub-path under BasePath even if
    /// the recorded PathBase differs (files moved to a new location or new computer).
    /// </summary>
    public bool IgnoreBasePath { get; set; }

    /// <summary>
    /// When true, skip SHA-512 recomputation. Only size and modification time are compared.
    /// BitRotSuspect cannot be detected in this mode.
    /// </summary>
    public bool SkipHashCheck { get; set; }

    public static void CheckSanity(MyDupFinderCheckJobDTO dto)
    {
        if (dto.ScanJobDTO is null)
        {
            throw new ParameterException("Param ScanJobDTO may not be null");
        }

        MyDupFinderScanJobDTO.CheckSanity(dto.ScanJobDTO);

        //Rule: DatabaseFile must exist (we are checking an existing database)
        if (!System.IO.File.Exists(dto.ScanJobDTO.DatabaseFile))
        {
            throw new ParameterException($"DatabaseFile must exist for check! {dto.ScanJobDTO.DatabaseFile}");
        }
    }

    public static void FixDto(MyDupFinderCheckJobDTO dto) => MyDupFinderScanJobDTO.FixDto(dto.ScanJobDTO);
}
