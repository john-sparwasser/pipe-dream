namespace PipeDream;

/// <summary>
/// The level-HEIGHT half of Lunar Magic's level-entry engine (blocks A/B are in the main file):
/// block C — LM's sprite-stream loader with its per-screen index cache, spawn-range tables and
/// the extended sprite format — three small blocks, twenty-one hooks, and the in-place edits
/// that make the rest of the game read the height from RAM: the object engine's column stride
/// (#$1B0 → $13D7), LM's extended-object handlers (ext 01/03 set $8B, the 32-row band a
/// tall level places objects in), the loader's plane-pointer maps repointed at the RAM tables
/// block B builds (and vanilla's ROM→RAM copy at $00A873 skipped), the block-probe tables the
/// same way ($BA60… → $0CB6…), the sprite engine's Y compares, and the layer-scroll bounds.
///
/// Same rule as A/B: after.smc's bytes verbatim, blocks at the same in-bank offsets in bank $1F
/// with only long-operand bank bytes changed (five in block C, none in the others — diffed against
/// ShaoBase's copies at $10F8E9/$10F899/$10F8C1/$10FCA9). The in-place edits carry no
/// LM addresses. Everything else LM changes in banks 00-03 belongs to other features (acts-like
/// pointers, VRAM/palette, overworld exits, layer-2 scroll code in LM's own bank $1F) and is left
/// alone — reference/LM_PARITY.md has the list.
/// </summary>
internal static partial class LmLevelEntry
{
    public const int BlockCSnes = (Bank << 16) | 0x8EED, BlockDSnes = (Bank << 16) | 0x8E9D,
                     BlockESnes = (Bank << 16) | 0x8EC5, BlockFSnes = (Bank << 16) | 0x92AD;
    private static readonly int[] BankBytesC = [0x086, 0x0AE, 0x0B8, 0x1CF, 0x1DA];

    public static byte[] BlockC() => Relocated(HexC, BankBytesC);
    public static byte[] BlockD() => Convert.FromHexString(HexD);
    public static byte[] BlockE() => Convert.FromHexString(HexE);
    public static byte[] BlockF() => Convert.FromHexString(HexF);

    /// <summary>The height half's hooks — every JSL/JML in the span gets its bank byte
    /// rewritten (the 12-byte $02A830 span is three JMLs).</summary>
    public static IEnumerable<(int Snes, byte[] Bytes)> HeightHooks()
    {
        foreach (var (site, hex) in new[]
        {
            (0x02A826, "5CA39010EAEA"),
            (0x02A830, "5C9891105C7B91105C2D8F10"),
            (0x02A846, "5C859110"),
            (0x02A95B, "5C708F10"),
            (0x02AA61, "5C668F10"),
            (0x02AB54, "5C5B8F10EA"),
            (0x02ABD0, "5C508F10EA"),
            (0x02AC64, "228B8F10"),
            (0x02ACA4, "228B8F10"),
            (0x02AF3D, "22498F10"),
            (0x02AFA7, "22498F10"),
            (0x02BA4E, "22C58E10"),
            (0x02D040, "22FD8E10"),
            (0x02D158, "22D28E10"),
            (0x02FEDC, "22FD8E10"),
            (0x01AC46, "22FD8E10"),
            (0x03B872, "22FD8E10"),
            (0x00AF71, "226592108011"),
            (0x00BEE8, "5C9D8E10CCD713"),
            (0x05D7B9, "22AD9210"),
            (0x03BCDC, "5CAD9210"),
        })
        {
            var b = Convert.FromHexString(hex);
            for (int i = 0; i + 3 < b.Length; i += 4)
                if (b[i] is 0x22 or 0x5C && b[i + 3] == 0x10) b[i + 3] = Bank;
            yield return (site, b);
        }
    }

    /// <summary>In-place edits, byte-for-byte LM's — see the type doc for what each group does.</summary>
    public static IEnumerable<(int Snes, byte[] Bytes)> InPlacePatches()
    {
        foreach (var (site, hex) in new[]
        {
            (0x0DA963, "C221A56B6DD713"),
            (0x0DA96B, "6B856EA90000E220EA"),
            (0x0DA9D6, "C220A56B38EDD713856B856E8504A90000E220CEA11B60EAEAC221A56B6DD713"),
            (0x0DA9F7, "6B"),
            (0x0DA9F9, "6E8504A90000E220EEA11B60"),
            (0x0DBB16, "20EFA9C457B00AC66CC66F8004"),
            // the object loader: reset the band at layer start ($0583C7 -> $0DE1F0) and add $8A (band x
            // 0x200 bytes) to the plane pointer of every object ($0586A1) — how a tall level places past row 31
            (0x0583C7, "22F0E10D"),
            (0x0586A1, "C221B700658A"),
            (0x0586A8, "6BB70D658A856EE220C8"),
            (0x0DA112, "D0E10DB0E10DE0E10D"),
            (0x0DE1AC, "4C4D0001A50A291FAAC220A765E665E665E2209DB819EB9DD81960FFFFFFFFFF4C4D0001A50B0A858BA50A291F8D28198DA11B60A50A291F0A858BA50B80EEFF4C4D0001648A648BA7656BFFFFFFFFFF4C4D0001FFFFFFFF"),
            (0x00BDA8, "F60BF60BF60B"),
            (0x00BDC0, "F60B"),
            (0x00BDC4, "F60BF60B"),
            (0x00BDCA, "F60B"),
            (0x00BDE4, "F60BF60B260C260C260C"),
            (0x00BE00, "260C"),
            (0x00BE04, "260C260C"),
            (0x00BE0A, "260C"),
            (0x00BE24, "260C260C560C560C560C"),
            (0x00BE40, "560C"),
            (0x00BE44, "560C560C"),
            (0x00BE4A, "560C"),
            (0x00BE64, "560C560C860C860C860C"),
            (0x00BE80, "860C"),
            (0x00BE84, "860C860C"),
            (0x00BE8A, "860C"),
            (0x00BEA4, "860C860C"),
            (0x00A873, "8045EAEA"),
            (0x00F2DB, "EAEA"),
            // level init: $192A high bits -> water/slippery and FG-vs-height ($05DD00), FG/BG byte
            // consumed ($0EF560), and the layer-2 position from $F9 set by the $009708 hook
            (0x00A6B8, "2260F50E"),
            (0x00A6CC, "2200DD05"),
            (0x00D2B2, "A5F9"),
            (0x05DD00, "2C2A191004A98085865004A9018585A9C01C2A19C220A51CC506E2206B"),
            (0x0EF560, "9CCD1364FE64FF847684896B"),
            (0x00A2AF, "C2209C8818AD8718F01ACE87182903000AA8B9CEA12CF40B50023A3A8D881818651C851CE220"),
            (0x00F478, "CDD713"),
            (0x00F47E, "68"),
            (0x00F488, "5E"),
            (0x00F493, "B60C"),
            (0x00F49B, "D60C"),
            (0x00F4F3, "CDD713"),
            (0x00F50E, "C60C"),
            (0x00F516, "E60C"),
            (0x0194D6, "CDD713"),
            (0x019501, "B60C"),
            (0x01950A, "C60C"),
            (0x019513, "D60C"),
            (0x01951C, "E60C"),
            (0x01AC40, "EAEAEB"),
            (0x01C08C, "EAEAEAEAEAEAEAEA"),
            (0x01C0E2, "FF"),
            (0x01D97C, "B60C"),
            (0x01D982, "D60C"),
            (0x0292FA, "B60C"),
            (0x029302, "C60C"),
            (0x02930B, "D60C"),
            (0x029313, "E60C"),
            (0x0295ED, "B60C"),
            (0x0295F5, "C60C"),
            (0x0295FE, "D60C"),
            (0x029606, "E60C"),
            (0x02A67A, "29"),
            (0x02A67C, "EBBD1517C2216908"),
            (0x02A685, "859829F0FFCDD713E2208500EB8502EA"),
            (0x02A6BB, "B60C"),
            (0x02A6C3, "C60C"),
            (0x02A6CC, "D60C"),
            (0x02A6D4, "E60C"),
            (0x02A84D, "E6"),
            (0x02A8D8, "4C"),
            (0x02A968, "6B"),
            (0x02A9D7, "4C38A8"),
            (0x02ABF3, "7F"),
            (0x02BA72, "B60C"),
            (0x02BA7A, "C60C"),
            (0x02BA83, "D60C"),
            (0x02BA8B, "E60C"),
            (0x02D03A, "EAEAEB"),
            (0x02D18D, "B60C"),
            (0x02D195, "C60C"),
            (0x02D19E, "D60C"),
            (0x02D1A6, "E60C"),
            (0x02FED6, "EAEAEB"),
            (0x03B86C, "EAEAEB"),
            (0x03D793, "6DD713"),
        })
            yield return (site, Convert.FromHexString(hex));
    }

    private const string HexC =
            "40FFD0FF80FF01C0B00120016001FF3FEBC220CDD713102538E51CCDF20B100BCDF00B3006E2A06BA9FF6BE220BD7A16" +
            "2904D0F4AD9B0DC980F0ED4A6BE2206BA505C97BD00FA554290D4BF4418FF4C9805C9C8301C8A6025CDAA9022901050A" +
            "85096B2901050A9D93175CD5AB022901050A99B80F5C59AB02050A9D2A1ECA5C65AA0222828F102901050A2204FD00A5" +
            "005C6AA902B7CE4829F05C60A902A9018555ADF40B29030AAAC221BFED8E10E90F008DF00BBFF58E10186910008DF20B" +
            "A9FFFF8DEE0BE220AF0CF30E850CAF0DF30E850DAF0EF30E850EAF0FF30E1869BE850FC230A0010064006402A5CE8DF6" +
            "0C3A3A8504A90000850685088D360DE220840AA5088507B7CEC9FFD018ADF50B8920F07AC8B7CEC9FFF00AC9FEF06F0A" +
            "8506C880E2AA0A0A0A29100A8502C8B7CE290F0A0402C8A50FD0198A290C4A4AEBB7CE5AA8B70CE902C22129FF006301" +
            "A868E220C8E608A502C500F0AAC220A600BDF60CE8E89DF60CE40290F7A600BD360DE8E89D360DE40290F7A50A65049D" +
            "F60CA5069D360DE22086004CEE8FA600E03E00F01EC220BDF60CE8E89DF60CE03E00D0F6A600BD360DE8E89D360DE03E" +
            "00D0F6E2306B8501ADF40B29030AAAC221A5008550A51C29F0FF85467FED8E1085528548A546187FF58E10854638E910" +
            "00854AA51A29F0FF38E93000854C18695001854EE220A549290114490448A54B2901144B044AA55B29010AAAA9A08545" +
            "7445ADF40B1035A550CDEE0B8DEE0BD004A9400445A548CDEF0B8DEF0BD004A9200445A9602545C960F03FC920D00DA5" +
            "51300F0AC940900CA23E8009B51B3A0A1002A900AAC220D4CEBDF60C85CEBD360DE220850AEBAA4BF46091F488B8A001" +
            "5C2CA8026885CE6885CF5C4BA802ADF50B8920F009C8B7CE1008C9FFF0215C4BA8020A850AC88011E8C8C8100C981865" +
            "CE85CEA0009002E6CFB7CEC9FFF0CF85540A0A0A29108502C8B7CE8500290F05028501C5511062A90F1400A920244530" +
            "3AD038A50AC549D024A55429F1C548F00AC54AD026A50AC54BD020C220A500C54C3016C54EE22010125C56A802C54BD0" +
            "0AA55429F1C54AF0E2E2205C46A80224453012C54FF002B00CA9202545D006A90F140080AE5C4BA802D0E4A50029F085" +
            "00C550D096244530197090A5542901050AEBA55429F0C220C55230BDC546E22010B95C56A802585350524954452D4745" +
            "4E31202020202020202020204C4D0101";

    private const string HexD =
            "8599A40CC00002B0045CF1BE005CBBBE0020202020202020202020204C4D0001";

    private const string HexE =
            "A509CDD813D005A401CCD7136B6900CDD813D005A400CCD7136B20204C4D0101";

    private const string HexF =
            "A55B4A901EA6971002A2006BE230A6956BA001A2208059A002A2108053A003A20A804DA59730E5C2203BEBAAADD7134A" +
            "4A4A4AC9350090D4E030B06BC9C101B0D0C92B01B0D1C9E100B0D2AAA980038D04428E0642A300A300EAA92000AC1442" +
            "8D04428C0642A300A300C2008AAE14428D04428E06428600A5964A4A4A4AAE14428D04428E0642A300A300A300AE1442" +
            "E4009003A600CA8E02428C0342E230A595186D1642AA6BA2018E5022C230AAA980038D51228E5322EAA92000AC06238D" +
            "51228C5322EA8AAE06238D51228E53228600A5964A4A4A4AAE06238D51228E5322EAEAAE0623E4009003A600CA9C5022" +
            "8E51228C5322E230A595186D0623AA6BFFFFFFFFFFFFFFFFFFFFFFFF4C4D0001";
}