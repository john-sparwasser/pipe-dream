using Xunit;
using PipeDream.Music;

namespace PipeDream.Tests;

/// <summary>
/// Music, phases 2 and 3 (reference/AMK_PORT.md): MML read into a tree with errors that say where, and compiled to the
/// bytes AddmusicK writes. The golden vectors are AddmusicK's own output for small songs, so they run without it;
/// the corpus test runs its whole set through our compiler and compares with what <c>addmusick</c> makes, byte for byte.
/// </summary>
public class MmlTests
{
    private const int Base = 0x238D;                    // where AddmusicK loads a song in ARAM

    private static CompiledSong Compile(string mml, Func<string, int>? groups = null)
        => MmlCompiler.Compile(MmlParser.Parse(mml), Base, groups);

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    /// <summary>A song and the bytes AddmusicK made of it (the song as loaded at $238D).</summary>
    public static readonly TheoryData<string, string> Golden = new()
    {
        { "#amk 2\n#0 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA400" },
        // AddmusicK's own originals: q written again even unchanged, ? before the channels or as ?0 (?1 does nothing), * recalling
        // another channel's loop, replacements before the channels.
        { "#amk 2\n#0 q6d c4 q6d c4 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400306DA4306DA4A400" },
        { "#amk 2\n?\n#0 c4", "91230000A1230000000000000000000000000000F408FA0400307FA400" },
        { "#amk 2\n#0 ?0 c4", "91230000A1230000000000000000000000000000F408FA0400307FA400" },
        { "#amk 2\n#0 ?1 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA400" },
        { "#amk 2\n#0 (1)[c4]2 [d4]2 \n#1 (1)2 *2", "9323FF008D23A323B123000000000000000000000000F408FA0400E9BA2302E9BE230200E9BA2302E9BE230200307FA400307FA600" },
        { "#amk 2\n\"NAME=$E7 $80\"\n#0 NAME c4", "9323FF008D23A3230000000000000000000000000000F408FA0400E780307FA400" },
        // Addmusic 4.05 and AddmusicM songs as AddmusicK converts them: the header's echo-buffer writes dropped, $ED $80 r v as $F6,
        // $ED $81 a b as $FA $02 a, stray bytes under $80 dropped, notes allowed before the first channel (channel 0's).
        { "#am4\n$ED $80 $6D $2B\n$ED $80 $7D $00\n$F0\n#0 c4", "9323FF008D23A3230000000000000000000000000000FA0400F0307FA400" },
        { "#am4\n#0 $ED $80 $1F $16 $ED $81 $05 $06 $10 c4", "9323FF008D23A3230000000000000000000000000000FA0400F61F16FA0205307FA400" },
        { "#am4\nl8 c\n#0 c4", "9323FF008D23A3230000000000000000000000000000FA0400187FA430A400" },
        { "#amm\n#0 $ED $81 $05 $06 c4", "9323FF008D23A3230000000000000000000000000000FA0400ED8105307FA400" },
        // Anything but notes before the first channel is channel 0's; loops are laid out in the order written, whichever channel;
        // a tie written straight against $DD is its own.
        { "#amk 2\nw220\n#0 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400E0DC307FA400" },
        { "#amk 2\n#1 [d4]2 f4\n#0 [c4]2 e4", "9323FF008D23A323B023000000000000000000000000F408FA0400E9BC2302307FA800E9B82302307FA900307FA600307FA400" },
        { "#amk 2\n#0 c32^64$DD$01$03$AD r32^64$DD$01$03$AD c32^64 $DD$01$03$AD", "9323FF008D23A3230000000000000000000000000000F408FA0400067FA403C6DD0103AD06C703C7DD0103AD09A4DD0103AD00" },
        // A sample load names the sample by its place after the group's samples, as the instrument table does.
        { "#amk 2\n#samples { #default \"06_scores_00.brr\" }\n#0 (\"06_scores_00.brr\",$03) c4", "9323FF008D23A3230000000000000000000000000000F408FA0400F31403307FA400" },
        { "#amk 2\n#samples { #optimized \"06_scores_00.brr\" \"06_scores_01.brr\" }\n#0 (\"06_scores_01.brr\",$03) c4", "9323FF008D23A3230000000000000000000000000000F408FA0400F31503307FA400" },
        { "#amk 2\n#0 c4 d4 e4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4A6A800" },
        { "#amk 2\n#0 c1", "9323FF008D23A3230000000000000000000000000000F408FA0400607FA4C600" },
        { "#amk 2\n#0 c=129", "9323FF008D23A3230000000000000000000000000000F408FA04007F7FA402C600" },
        { "#amk 2\n#0 c=200", "9323FF008D23A3230000000000000000000000000000F408FA0400647FA4C600" },
        { "#amk 2\n#0 c=384", "9323FF008D23A3230000000000000000000000000000F408FA04007F7FA4C6C603C600" },
        { "#amk 2\n#0 c2^2^2^2", "9323FF008D23A3230000000000000000000000000000F408FA04007F7FA4C6C603C600" },
        { "#amk 2\n#0 c4.^8", "9323FF008D23A3230000000000000000000000000000F408FA0400607FA400" },
        { "#amk 2\n#0 c4 r4 r4 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA460C718A400" },
        { "#amk 2\n#0 r=128", "9323FF008D23A3230000000000000000000000000000F408FA0400407FC7C700" },
        { "#amk 2\n#0 c8 c4 c8 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400187FA430A418A4A400" },
        { "#amk 2\n#0 c4 q50 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA43050A400" },
        { "#amk 2\n#0 c4 v100 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4E764A400" },
        { "#amk 2\n#0 o2 c < c > c", "9323FF008D23A3230000000000000000000000000000F408FA0400187F8C808C00" },
        { "#amk 2\n#0 h2 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA600" },
        { "#amk 2\n#0 c+4 d-4 b+4 c-4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA5A5B0A300" },
        { "#amk 2\n#0 @2 c4 @0 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400DA02307F9FDA00A400" },
        { "#amk 2\n#0 @21 c4 d4 @22 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FD0A6D100" },
        { "#amk 2\n#0 y10,1,0 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400DB8A307FA400" },
        { "#amk 2\n#0 t40 w100 v200 p24,12,8 n1F c4", "9323FF008D23A3230000000000000000000000000000F408FA0400E228E064E7C8DE180C08F81F307FA400" },
        { "#amk 2\n#0 {c8 d8 e8} {c16 d16}", "9323FF008D23A3230000000000000000000000000000F408FA0400107FA4A6A808A4A600" },
        { "#amk 2\n#0 c4&d4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4DD0030A6A600" },
        { "#amk 2\n#0 c8&d4", "9323FF008D23A3230000000000000000000000000000F408FA0400187FA4DD0018A630A600" },
        { "#amk 2\n#0 c4&d8&e8", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4DD0030A618A6DD0018A8A800" },
        { "#amk 2\n#0 [c8]3", "9323FF008D23A3230000000000000000000000000000F408FA0400E9AD230300187FA400" },
        { "#amk 2\n#0 [c4 d8]2 e4", "9323FF008D23A3230000000000000000000000000000F408FA0400E9B02302307FA800307FA418A600" },
        { "#amk 2\n#0 c4 [d8 e8]3 f4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4E9B32303307FA900187FA6A800" },
        { "#amk 2\n#0 (1)[c8]2 d4 (1)3", "9323FF008D23A3230000000000000000000000000000F408FA0400E9B42302307FA6E9B4230300187FA400" },
        { "#amk 2\n#0 [[c8]]3 d4", "9323FF008D23A3230000000000000000000000000000F408FA0400E600187FA4E602307FA600" },
        { "#amk 2\n#0 c4 [[c8 [d8]2 e8]]2", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4E600187FA4E9BA2302187FA8E60100187FA600" },
        { "#amk 2\n#0 c4\n#1 d4", "9323FF008D23A323AC23000000000000000000000000F408FA0400307FA400307FA600" },
        { "#amk 2\n#0 c4\n#2 c4\n#7 c4", "9323FF008D23A3230000AC230000000000000000B023F408FA0400307FA400307FA400307FA400" },
        { "#amk 2\n#0 c4\n\n#0 d4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA430A600" },
        { "#amk 2\n#0 c4 / d4", "9523A523FF008F23B5230000000000000000000000000000BD230000000000000000000000000000F408FA0400307FA430A600" },
        { "#amk 2\n#0 c4 / d4\n#1 e4 f4", "9523A523FF008F23B523C023000000000000000000000000BD23C023000000000000000000000000F408FA0400307FA430A600307FA8A900" },
        { "#amk 2\n#0 c4 d4\n#1 e4 / f4", "9523A523FF008F23B523BF23000000000000000000000000B523C223000000000000000000000000F408FA0400307FA4A600307FA830A900" },
        { "#amk 2\n#0 c4 ?", "91230000A1230000000000000000000000000000F408FA0400307FA400" },
        { "#amk 2\n#0 ?\nc4", "91230000A1230000000000000000000000000000F408FA0400307FA400" },
        { "#amk 2\n#0 $EF $01 $02 $03 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400EF020301307FA400" },
        { "#amk 2\n#0 $DA $02 c4 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400DA02307F9F9F00" },
        { "#amk 2\n#0 $E6 $00 c8 $E6 $01 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400E600187FA4E601187FA400" },
        { "#amk 2\n#0 q50 c4 / d4 c4", "9523A523FF008F23B5230000000000000000000000000000BD230000000000000000000000000000F408FA04003050A430A6A400" },
        { "#amk 2\n#0 c4 [d8]2\n#1 (1)[e8]2\n#2 (1)3", "9323FF008D23A323B023B52300000000000000000000F408FA0400307FA4E9BA230200E9BE230200E9BE230300187FA600187FA800" },
        { "#amk 2\n#samples { \"06_scores_00.brr\" }\n#instruments { \"06_scores_00.brr\" $FF $E0 $B8 $04 $00 }\n#0 @30 c4", "9923FF008D2300FFE0B80400A9230000000000000000000000000000F408FA0400DA1E307FA400" },
        { "#amk 2\n#samples { #default \"06_scores_01.brr\" }\n#instruments { \"06_scores_01.brr\" $FF $E0 $B8 $04 $00 @5 $FF $E0 $B8 $04 $00 n1F $FF $E0 $B8 $04 $00 }\n#0 @30 c4 @31 c4 @32 c4", "A523FF008D2314FFE0B8040007FFE0B804009FFFE0B80400B5230000000000000000000000000000F408FA0400DA1E307FA4DA1FA4DA20A400" },
        { "#amk 2\n#samples { \"06_scores_00.brr\" }\n#0 (\"06_scores_00.brr\", $04) c4", "9323FF008D23A3230000000000000000000000000000F408FA0400F30004307FA400" },
        { "#amk 2\n#option noloop\n#0 c4", "91230000A1230000000000000000000000000000F408FA0400307FA400" },
        { "#amk 2\n#option tempoimmunity\n#0 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400F407307FA400" },
        { "#amk 2\n#option smwvtable\n#0 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400FA0600307FA400" },
        { "#amk 2\n#halvetempo\n#0 t40 c4 c=5 c16 v100 $DC $10 $20 $E1 $10 $20 $E3 $10 $20 $E8 $10 $20 $DD $10 $20 $A4 $DE $10 $20 $30 $E5 $10 $20 $30 $EA $10 $EB $10 $20 $05 $EC $10 $20 $05 $F2 $10 $20 $30 $F4 $01 p24,12,8 q50 c4 [c8]2 y10", "9323FF008D23A3230000000000000000000000000000F408FA0400E214187FA403A406A4E764DC0820E10820E30810E80820DD0810A4DE084030E5084030EA08EB081005EC081005F2082030F401DE0C18081850A4E9E92302DB0A000C50A400" },
        { "#amk 2\n#option dividetempo 3\n#0 t40 c4 c=5 c16 v100 $DC $10 $20 $E1 $10 $20 $E3 $10 $20 $E8 $10 $20 $DD $10 $20 $A4 $DE $10 $20 $30 $E5 $10 $20 $30 $EA $10 $EB $10 $20 $05 $EC $10 $20 $05 $F2 $10 $20 $30 $F4 $01 p24,12,8 q50 c4 [c8]2 y10", "9323FF008D23A3230000000000000000000000000000F408FA0400E20D107FA402A404A4E764DC0520E10520E3050AE80520DD050AA4DE056030E5056030EA05EB050A05EC050A05F2052030F401DE0824081050A4E9E92302DB0A000850A400" },
        { "#amk 2\n#option tempoimmunity\n#option smwvtable\n#0 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400F407FA0600307FA400" },
        { "#amk 2\n#option smwvtable\n#option tempoimmunity\n#0 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400FA0600F407307FA400" },
        { "#amk 2\n#0 c5^5", "9323FF008D23A3230000000000000000000000000000F408FA04004D7FA400" },
        { "#amk 2\n#0 c5^c5", "9323FF008D23A3230000000000000000000000000000F408FA04003E7FA426A400" },
        { "#amk 2\n#0 c7^7^7", "9323FF008D23A3230000000000000000000000000000F408FA0400527FA400" },
        { "#amk 2\n#0 c11.^11.", "9323FF008D23A3230000000000000000000000000000F408FA0400347FA400" },
        { "#amk 2\n#0 {c7^7}", "9323FF008D23A3230000000000000000000000000000F408FA0400257FA400" },
        { "#amk 2\n#halvetempo\n#0 c5^5", "9323FF008D23A3230000000000000000000000000000F408FA0400267FA400" },
        { "#amk 2\n#halvetempo\n#0 c7.", "9323FF008D23A3230000000000000000000000000000F408FA0400157FA400" },
        { "#amk 2\n#halvetempo\n#0 {c7}", "9323FF008D23A3230000000000000000000000000000F408FA0400097FA400" },
        { "#amk 2\n#halvetempo\n#0 {c=5^=5}", "9323FF008D23A3230000000000000000000000000000F408FA0400037FA400" },
        { "#amk 2\n#option dividetempo 3\n#0 c5.", "9323FF008D23A3230000000000000000000000000000F408FA0400137FA400" },
        { "#amk 2\n#0 c4 c16 r16 r8 c16 r16 r8", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA40CA424C70CA424C700" },
        { "#amk 2\n#0 c4 $DD $20 $33 $9B ^6 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4DD20339B20C630A400" },
        { "#amk 2\n#0 h5 c8 [c8]2 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400187FA9E9B32302187FA900187FA400" },
        { "#amk 2\n#0 [h5 c8]2 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400E9B02302187FA400187FA900" },
        { "#amk 2\n#0 [h5 w68]2 c8 [c8]2", "9323FF008D23A3230000000000000000000000000000F408FA0400E9B42302187FA4E9B7230200E04400187FA900" },
        { "#amk 2\n#0 @15 c8 [c8]2 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400DA0F187F9CE9B52302187F9C00187F9C00" },
        { "#amk 2\n#0 [@15 c8]2 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400E9B02302187FA400DA0F187F9C00" },
        { "#amk 2\n#0 h5 @15 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400DA0F187FA900" },
        { "#amk 2\n#0 @10 c8 h-5 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400DA0A187F9E9F00" },
        { "#amk 2\n#0 @15 [h5 c8]2 c8 [c8]2 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400DA0FE9B92302187F9CE9BD2302187F9C00187FA900187FA900" },
        { "#amk 2\n#0 @21 [c8 d8]2 e8", "9323FF008D23A3230000000000000000000000000000F408FA0400E9B02302187FD000187FD0A600" },
        { "#amk 2\n#0 [@21 c8]2 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400E9B02302187FA400187FD000" },
        { "#amk 2\n#0 @21 [[c8 d4]]2", "9323FF008D23A3230000000000000000000000000000F408FA0400E600187FD030A6E60100" },
        { "#amk 2\n#0 c4 & v100 d4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4E764DD0030A6A600" },
        { "#amk 2\n#0 c4 & o5 d4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4DD0030B2B200" },
        { "#amk 2\n#0 c4 & r4 d4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4DD0030C7C7A600" },
        { "#amk 2\n#0 c8 ^8 & d8", "9323FF008D23A3230000000000000000000000000000F408FA0400187FA4C6DD0018A6A600" },
        { "#amk 2\n#0 c8 ^8 ^8 & d8", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA418C6DD0018A6A600" },
        { "#amk 2\n#0 c1 & d4", "9323FF008D23A3230000000000000000000000000000F408FA0400417FA47FC6DD007FA630A600" },
        { "#amk 2\n#0 r=300 & d4", "9323FF008D23A3230000000000000000000000000000F408FA04002E7FC77FC7C7DD007FA630A600" },
        { "#amk 2\n#0 c=254 & d4", "9323FF008D23A3230000000000000000000000000000F408FA04007F7FA4C6DD007FA630A600" },
        { "#amk 2\n#0 c4 & [d8]2 e4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4E9B72302DD0000A8307FA800187FA600" },
        { "#amk 2\n#0 [c8]2\n#1 (1)[d8]2\n#2 (1)3", "9323FF008D23A323AD23B22300000000000000000000F408FA0400E9B7230200E9BB230200E9BB230300187FA400187FA600" },
        { "#amk 2\n#0 (1)[c8]2 (1) [d8]3", "9323FF008D23A3230000000000000000000000000000F408FA0400E9B52302E9B52301E9B9230300187FA400187FA600" },
        { "#amk 2\n#0 o1\n#1 c4", "9323FF008D23A323A923000000000000000000000000F408FA040000307FA400" },
        { "#amk 2\n#0 c4 r8 r8 r4", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA460C700" },
        { "#amk 2\n#0 ^8 ^8 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400307FC618A400" },
        { "#amk 2\n#option noloop\n#0 c4 / d4", "9323A3230000B3230000000000000000000000000000BB230000000000000000000000000000F408FA0400307FA430A600" },
        { "#amk 2\n#0 c4 ?\n#1 d4 / e4", "9323A3230000B323BC23000000000000000000000000B323BF23000000000000000000000000F408FA0400307FA400307FA630A800" },
        { "#amk 2\n#option tempoimmunity\n#3 c4", "9323FF008D23A32300000000A6230000000000000000F40700F408FA0400307FA400" },
        { "#amk 2\n#option smwvtable\n#option tempoimmunity\n#2 c4\n#5 d4", "9323FF008D23A3230000A92300000000B22300000000FA0600F40700F408FA0400307FA400307FA600" },
        { "#amk 2\n#halvetempo\n#0 c=1 d4 r195 e4", "9323FF008D23A3230000000000000000000000000000F408FA0400017FA418A6A800" },
        { "#amk 2\n#halvetempo\n#0 $E2 $05 $E3 $10 $41 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400E202E30820187FA400" },
        { "#amk 2\n#0 $F4 $09 c4 $FA $06 $00 c4", "9323FF008D23A3230000000000000000000000000000F408FA0400F409307FA4FA0600A400" },
        { "#amk 2\n#0 $E6 $00 c8 $E6 $01 c8 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400E600187FA4E601187FA4A400" },
        { "#amk 2\n#0 n1F @10 c8 n01 c8", "9323FF008D23A3230000000000000000000000000000F408FA0400F81FDA0A187F9EF8019E00" },
        { "#amk 2\n#0 q50 c4 / d4 c4", "9523A523FF008F23B5230000000000000000000000000000BD230000000000000000000000000000F408FA04003050A430A6A400" },
        { "#amk 2\n#0 c4 [d8 ^8]2 e8", "9323FF008D23A3230000000000000000000000000000F408FA0400307FA4E9B32302187FA800307FA600" }
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void a_song_compiles_to_the_bytes_addmusick_makes(string mml, string expected)
    {
        var r = Compile(mml);
        Assert.True(r.Ok, string.Join("; ", r.Diagnostics));
        Assert.Equal(expected.ToUpperInvariant(), Hex(r.Bytes));
    }

    [AmkFact]
    public void the_whole_corpus_compiles_to_the_bytes_addmusick_makes()
    {
        string root = AmkOracle.Root!;
        var theirs = AmkOracle.Compile(root, AmkOracle.Corpus);
        foreach (string file in AmkOracle.Corpus)
        {
            var ours = Compile(File.ReadAllText(Path.Combine(root, "music", file)));
            Assert.True(ours.Ok, $"{file}: {string.Join("; ", ours.Diagnostics)}");
            var ram = theirs[Path.GetFileNameWithoutExtension(file)].Ram;
            Assert.True(ram.AsSpan(Base, ours.Bytes.Length).SequenceEqual(ours.Bytes), $"{file}: the bytes differ from AddmusicK's");
        }
    }

    // ---- the parser ----

    [Fact]
    public void a_song_without_a_target_or_without_data_is_refused_as_addmusick_does()
    {
        Assert.Contains("target program", Assert.Single(MmlParser.Parse("#0 c4").Diagnostics).Message);
        Assert.Contains("any data", Assert.Single(MmlParser.Parse("#amk 2").Diagnostics).Message);
        Assert.Contains("inside a channel", MmlParser.Parse("#amk 2\nc4").Diagnostics[0].Message);
    }

    [Fact]
    public void a_replacement_can_name_a_loop_count()               // AddmusicK 1.0.6's own Special World; 1.0.5 refuses it
        => Assert.Equal(Hex(Compile("#amk 2\n#0 [c4]8").Bytes), Hex(Compile("#amk 2\n\"N=8\"\n#0 [c4]N").Bytes));

    [Fact]
    public void remote_code_is_not_a_loop_to_recall()
        => Assert.Contains("no loop to recall", Assert.Single(Compile("#amk 2\n(!1)[$F4 $09]\n#0 c4 *2").Diagnostics).Message);

    [Fact]
    public void a_negative_tick_length_is_an_error()
    {
        Assert.Contains("note length", Assert.Single(MmlParser.Parse("#amk 2\n#0 c=-5 d4").Diagnostics).Message);
        Assert.True(Compile("#amk 2\n#0 c=0 d4").Ok);       // zero is allowed, as AddmusicK allows it
    }

    [Fact]
    public void errors_say_the_line_and_column()
    {
        var d = MmlParser.Parse("#amk 2\n#0 c4 C4\n").Diagnostics;
        Assert.Equal((2, 7), (d[0].Line, d[0].Column));
        Assert.Contains("Unexpected character \"C\"", d[0].Message);
        Assert.Equal((2, 4), (MmlParser.Parse("#amk 2\n#0 o7 c4").Diagnostics[0].Line, MmlParser.Parse("#amk 2\n#0 o7 c4").Diagnostics[0].Column));
    }

    [Fact]
    public void values_out_of_range_are_errors_not_clamped()
    {
        foreach (var bad in new[] { "o0 c", "o7 c", "l0 c", "l193 c", "q3 c", "[c8]0", "[c8]300", "$FF c", "$A4", "v256 c", "h200 c" })
            Assert.False(Compile("#amk 2\n#0 " + bad).Ok, bad);
        Assert.False(Compile("#amk 2\n#0 o6 b").Ok);                 // a note past the top of the range
        Assert.False(Compile("#amk 2\n#0 [c8 [d8]2]2").Ok);           // loops do not nest; [[ ]] does
        Assert.True(Compile("#amk 2\n#0 [[c8 [d8]2]]2").Ok);
    }

    [Fact]
    public void the_preprocessor_keeps_the_branch_taken_and_the_line_numbers()
    {
        var song = MmlParser.Parse("#amk 2\n#define A 3\n#if A >= 3\n#0 c4\n#endif\n#ifdef B\n#0 d4 d4\n#endif\n#ifndef B\n#0 e4\n#endif");
        Assert.Empty(song.Diagnostics);
        Assert.Equal(2, song.Channels[0]!.OfType<NoteCmd>().Count());                   // c4 and e4, not the two d4
        Assert.Contains("#error: no", MmlParser.Parse("#amk 2\n#0 c4\n#error no").Diagnostics[0].Message);
        Assert.Contains("Missing #endif", MmlParser.Parse("#amk 2\n#0 c4\n#if X == 1").Diagnostics[0].Message);
        Assert.Equal(5, MmlParser.Parse("#amk 2\n#ifdef Q\n#0 c4\n#endif\n#0 C4").Diagnostics[0].Line);     // lines survive a skipped branch
    }

    [Fact]
    public void a_replacement_applies_to_the_text_after_it_and_the_longest_name_wins()
    {
        var r = Compile("#amk 2\n#0\n\"cymbal=@21\" \"c=d\" \"cymbal2=@22\"\ncymbal c4 cymbal2 c4");
        Assert.True(r.Ok, string.Join("; ", r.Diagnostics));
        Assert.Equal(Compile("#amk 2\n#0 @21 d4 @22 d4").Bytes, r.Bytes);
    }

    [Fact]
    public void directives_are_read_whole()
    {
        var s = MmlParser.Parse("""
            #amk 2
            #spc { #author "A" #title "T" #length "2:00" }
            #samples { #default "a.brr" "b.brr" }
            #instruments { "a.brr" $FF $E0 $B8 $04 $00 @5 $01 $02 $03 $04 $05 n1F $80 $81 $82 $83 $84 }
            #path "dir"
            #pad 2F0
            #option tempoimmunity
            #option dividetempo 3
            #halvetempo
            #0 c4
            """);
        Assert.Empty(s.Diagnostics);
        Assert.Equal(("A", "T", "2:00"), (s.Spc["author"], s.Spc["title"], s.Length));
        Assert.Equal(["default"], s.SampleGroups);
        Assert.Equal(["a.brr", "b.brr"], s.Samples);
        Assert.Equal(new (string?, int?, int?)[] { ("a.brr", null, null), (null, 5, null), (null, null, 0x1F) },
                     s.Instruments.Select(i => (i.Sample, i.FromInstrument, i.Noise)).ToArray());
        Assert.Equal(("dir", 0x2F0, true, 3), (s.Path, s.Pad, s.TempoImmunity, s.DivideTempo));
        Assert.True(s.HalveTempo);
    }

    [Fact]
    public void a_song_names_its_samples_after_the_group_it_uses()
    {
        string head = "#amk 2\n#samples { #default \"x.brr\" \"y.brr\" }\n#instruments { \"y.brr\" $FF $E0 $B8 $04 $00 }\n#0 @30 c4";
        var r = Compile(head);
        Assert.True(r.Ok);
        Assert.Equal(0x15, r.Bytes[6]);                                                  // 20 samples of #default, then y.brr second
        Assert.Equal(3 + 1, Compile(head, _ => 3).Bytes[6]);                              // a group of three: y.brr is still the second after it
    }
}
