using System.Text;

namespace PipeDream;

/// <summary>
/// Lunar Magic's SA-1 forms of the code the prep carries from it (reference/SA1_PORT.md, phase 2):
/// what an LM save writes into an SA-1 ROM where the LoROM save writes the bytes in LmLevelRender,
/// LmLevelEntry and RomPrep.Stamps. On SA-1 the same routines address the RAM the pack moved
/// ($0Bxx-$1Fxx → +$6000, $7E/$7F → $40/$41, $0005 → $3005), and one of them grows a loop that
/// mirrors LM's $7FC060 into BW-RAM for the SA-1 CPU; a sweep reproduces most of it and not all
/// (Sa1Remap.Code), so the bytes are LM's own, carried as a diff from the LoROM stamps.
///
/// The diff is GENERATED from oracle ROMs — clean + pack, saved by LM 3.40 — by <see cref="Generate"/>
/// (`--sa1lmforms`), against the stamps of <see cref="PrepVersion"/>, by three rules a byte at a time:
///
///  1. where our stamp carries LM's LoROM byte and LM's SA-1 save writes another, the SA-1 byte;
///  2. where the oracle holds the PACK's byte (LM left the pack's remap or hijack standing, as at
///     $02ABF3 where the pack's three JMLs own the spot LM's `LDX #$7F` takes on LoROM), the byte is
///     DROPPED from the SA-1 form: the pack's own output on our base stands, which a restamp would
///     corrupt — the pack's code is relocated by our RATS block at $108000, so LM's copy of the byte
///     is not even the right value;
///  3. a hook's JSL/JML target is ours (LM's copies of the blocks sit elsewhere in the oracle), and
///     a relocated block's in-bank pointers are ours — only its other bytes are compared.
///
/// <see cref="Apply"/> plays the diff onto the SA-1 overlay RomPrep.ApplySa1 writes. Format: tokens
/// `SNES=HEX` (those bytes, from that address) and `SNES=...` (that many bytes dropped).
/// </summary>
internal static class LmSa1Forms
{
    /// <summary>The prep version the diff was generated against. ApplySa1 refuses another: a stamp
    /// list that changed needs the diff regenerated and re-pinned.</summary>
    public const int PrepVersion = 35;

    /// <summary>From `.resources/sa1/lm-sa1.smc`: clean + SA-1 Pack v1.40, one level saved by Lunar
    /// Magic 3.40 — the bank-$1F engine, the level-entry blocks and hooks, the fixed blocks and
    /// in-place edits, the entrance routines, the acts-like core and the Map16 ladder. Pinned by
    /// Sa1FormsTests. What a plain save does not install (ExAnimation, midway, overworld palette,
    /// 4bpp, layer 3) is not here yet; reference/SA1_PORT.md lists the oracles still needed.</summary>
    public const string LevelSave = """
        00A2B3=78 00A2B6=78 00A2BB=78 00A2C6=6B 00A2CD=78 00A8A1=. 00BDA9=6B 00BDAB=6B
        00BDAD=6B 00BDC1=6B 00BDC5=6B 00BDC7=6B 00BDCB=6B 00BDE5=6B 00BDE7=6B 00BDE9=6C
        00BDEB=6C 00BDED=6C 00BE01=6C 00BE05=6C 00BE07=6C 00BE0B=6C 00BE25=6C 00BE27=6C
        00BE29=6C 00BE2B=6C 00BE2D=6C 00BE41=6C 00BE45=6C 00BE47=6C 00BE4B=6C 00BE65=6C
        00BE67=6C 00BE69=6C 00BE6B=6C 00BE6D=6C 00BE81=6C 00BE85=6C 00BE87=6C 00BE8B=6C
        00BEA5=6C 00BEA7=6C 00BEEE=73 00BF83=79 00C977=28 00F47A=73 00F494=6C 00F49C=6C
        00F4F5=73 00F50F=6C 00F517=6C 0194D8=73 019502=6C 01950B=6C 019514=6C 01951D=6C
        01D97D=6C 01D983=6C 0292FB=6C 029303=6C 02930C=6C 029314=6C 0295EE=6C 0295F6=6C
        0295FF=6C 029607=6C 02A67F=77 02A68C=73 02A6BC=6C 02A6C4=6C 02A6CD=6C 02A6D5=6C
        02ABF3=. 02BA73=6C 02BA7B=6C 02BA84=6C 02BA8C=6C 02D18E=6C 02D196=6C 02D19F=6C
        02D1A7=6C 03BB02=79 03BB05=79 03BB08=7B 03BB0B=74 03BCA2=61 03BCAB=7F 03BCAE=7F
        03BCE9=79 03BD05=73 03BD0C=79 03BD1F=73 03BD55=61 03BD58=6D 03BD5B=6D 03BD5E=6D
        03BD68=7D 03BD6B=7B 03BD75=7D 03BD81=6D 03BD87=73 03BD8A=7D 03D795=73 058DB3=30
        058DBB=30 058E14=30 05DCB9=7D 05DCBC=7D 05DD02=79 05DD13=79 05DD33=79 05DD41=73
        05DD4A=79 06F57C=79 06F5BC=79 06F5CE=6F 06F60A=6D 06F61A=76 06F633=76 0DA969=73
        0DA9DD=73 0DA9EB=7B 0DA9F5=73 0DAA03=7B 0DE1C1=79 0DE1C5=79 0DE1DB=79 0DE1DE=7B
        0DE1F0=C220A20EBF60C07F9F000041CACA10F4648AE220A7656B 0DE20C=4C4D0101 0DF2A9=000041 0DFEAE=7B 0DFEC2=7B 0DFEE5=73 0DFEED=73 0DFEF4=7B
        0DFF2A=7B 0EF554=61 0EF562=73 0EFD35=79 1E8153=74 1E815E=74 1E816B=6B 1E816E=74
        1E8179=74 1E817C=74 1E8185=74 1E818D=74 1E8196=74 1E819E=73 1E81A5=6B 1E81B2=73
        1E81BC=73 1E81C5=6B 1E81D2=74 1E81D7=78 1E8202=6B 1E8224=74 1E823A=6B 1E824C=73
        1E8253=6B 1E8265=74 1E826F=74 1E827A=6B 1E8295=6B 1E82C9=74 1E8325=74 1E83CC=73
        1E83CF=73 1E83E3=74 1E83EE=74 1E83FB=74 1E8434=74 1E8443=74 1E8464=74 1E846D=74
        1E8476=74 1E847F=74 1E8484=74 1E8488=74 1E8495=74 1E8498=74 1E84A4=74 1E84AD=74
        1E84B2=74 1E84B6=74 1E84C3=74 1E84C6=74 1E84DC=2DD3 1E851C=28D3 1E8553=74 1E8558=74
        1E855F=74 1E8570=74 1E857D=74 1E8580=74 1E8587=74 1E858B=74 1E858F=74 1E859E=74
        1E85A1=74 1E85A6=74 1E85A9=74 1E85AE=74 1E85BF=74 1E85C2=74 1E85C9=74 1E85CD=74
        1E85D1=74 1E85E0=74 1E85E3=74 1E85E8=74 1E85EB=74 1E85F1=78 1E8613=6B 1E8616=77
        1E8619=6B 1E861C=77 1E861F=6B 1E8622=6B 1E8627=6B 1E862C=6B 1E862F=6B 1E8641=6B
        1E8644=77 1E8D1B=6B 1E8D24=73 1E8D2B=79 1E8D47=73 1E8D4E=79 1E8D5C=6B 1E8D60=6D
        1E8D76=73 1E8D7E=73 1E8D86=6B 1E8D91=73 1E8DA2=6B 1E8DA4=40 1E8DAA=6B 1E8DB1=41
        1E8DB7=6C 1E8DCA=73 1E8DD1=79 1E8DE0=73 1E8DF6=6B 1E8DFD=6B 1E8E0B=73 1E8E12=79
        1E8E1E=6B 1E8E34=6B 1E8E46=6B 1E8E49=6C 1E8E4C=6B 1E8E4F=6C 1E8E68=6B 1E8E6B=6C
        1E8E6E=73 1E8EC9=73 1E8ED0=73 1E8ED6=73 1E8EDD=73 1E8F02=73 1E8F0A=6B 1E8F0F=6B
        1E8F1B=1676 1E8F23=6D 1E8F56=77 1E8F61=6F 1E8F6A=7E 1E8F91=6B 1E8FA1=6B 1E8FAC=6B
        1E8FB2=6B 1E8FDD=6C 1E8FEB=6D 1E8FFC=6B 1E9050=6C 1E9055=6C 1E905E=6D 1E9063=6D
        1E906E=6C 1E9073=6D 1E9086=6C 1E908B=6C 1E9095=6D 1E909A=6D 1E90A7=6B 1E9101=6B
        1E9108=6B 1E910B=6B 1E9116=6B 1E9119=6B 1E9148=6C 1E914D=6D 1E916D=6B 1E92DB=73
        1F8026=7B 1F802E=7C 1F8036=7B 1F803E=7C 1F8051=79 1F8067=7B 1F806F=7C 1F8077=7B
        1F807F=7C 1F8092=79 1F80A8=7B 1F80B0=7C 1F80B8=7B 1F80C0=7C 1F80D3=79 1F80E9=7B
        1F80F1=7C 1F80F9=7B 1F8101=7C 1F8114=79 1F812A=7B 1F8132=7C 1F813A=7B 1F8142=7C
        1F8155=79 1F816B=7B 1F8173=7C 1F817B=7B 1F8183=7C 1F8196=79 1F81AC=7B 1F81B4=7C
        1F81BC=7C 1F81C4=7C 1F81D7=79 1F81ED=7C 1F81F5=7C 1F81FD=7C 1F8205=7C 1F8218=79
        1F822E=7C 1F8236=7C 1F823E=7C 1F8246=7C 1F8259=79 1F826F=7C 1F8277=7C 1F827F=7C
        1F8287=7C 1F829A=79 1F82B0=7C 1F82B8=7C 1F82C0=7C 1F82C8=7C 1F82DB=79 1F82F1=7C
        1F82F9=7C 1F8301=7C 1F8309=7C 1F831C=79 1F8332=7C 1F833A=7C 1F8342=7C 1F834A=7C
        1F835D=79 1F8373=7C 1F837B=7C 1F8383=7C 1F838B=7C 1F839E=79 1F83B4=7C 1F83BC=7C
        1F83C4=7C 1F83CC=7C 1F83DF=79 1F83F5=7C 1F83FD=7C 1F8405=7C 1F840D=7C 1F8420=79
        1F8436=7C 1F843E=7C 1F8446=7C 1F844E=7C 1F8461=79 1F8477=7C 1F847F=7C 1F8487=7C
        1F848F=7C 1F84A2=79 1F84B8=7C 1F84C0=7C 1F84C8=7C 1F84D0=7C 1F84E3=79 1F84F9=7C
        1F8501=7C 1F8509=7C 1F8511=7C 1F8524=79 1F88DE=7C 1F88E8=7D 1F88F2=7C 1F88FC=7D
        1F890F=79 1F8927=7C 1F8931=7D 1F893B=7C 1F8945=7D 1F8958=79 1F8970=7C 1F897A=7D
        1F8984=7C 1F898E=7D 1F89A1=79 1F89B9=7C 1F89C3=7D 1F89CD=7C 1F89D7=7D 1F89EA=79
        1F8A02=7C 1F8A0C=7D 1F8A16=7C 1F8A20=7D 1F8A33=79 1F8A4B=7C 1F8A55=7D 1F8A5F=7C
        1F8A69=7D 1F8A7C=79 1F8A94=7D 1F8A9E=7D 1F8AA8=7D 1F8AB2=7D 1F8AC5=79 1F8ADD=7D
        1F8AE7=7D 1F8AF1=7D 1F8AFB=7D 1F8B0E=79 1F8B26=7D 1F8B30=7D 1F8B3A=7D 1F8B44=7D
        1F8B57=79 1F8B6F=7D 1F8B79=7D 1F8B83=7D 1F8B8D=7D 1F8BA0=79 1F8BB8=7D 1F8BC2=7D
        1F8BCC=7D 1F8BD6=7D 1F8BE9=79 1F8C01=7D 1F8C0B=7D 1F8C15=7D 1F8C1F=7D 1F8C32=79
        1F8C4A=7D 1F8C54=7D 1F8C5E=7D 1F8C68=7D 1F8C7B=79 1F8C93=7D 1F8C9D=7D 1F8CA7=7D
        1F8CB1=7D 1F8CC4=79 1F8CDC=7D 1F8CE6=7D 1F8CF0=7D 1F8CFA=7D 1F8D0D=79 1F8D25=7D
        1F8D2F=7D 1F8D39=7D 1F8D43=7D 1F8D56=79 1F8D6E=7D 1F8D78=7D 1F8D82=7D 1F8D8C=7D
        1F8D9F=79 1F8DB7=7D 1F8DC1=7D 1F8DCB=7D 1F8DD5=7D 1F8DE8=79 1F8E00=7D 1F8E0A=7D
        1F8E14=7D 1F8E1E=7D 1F8E31=79 1F8E49=7D 1F8E53=7D 1F8E5D=7D 1F8E67=7D 1F8E7A=79
        1F92B2=7B 1F92BA=7C 1F92C2=7B 1F92CA=7C 1F92E1=7B 1F92E9=7C 1F92F1=7B 1F92F9=7C
        1F9310=7B 1F9318=7C 1F9320=7B 1F9328=7C 1F933F=7B 1F9347=7C 1F934F=7B 1F9357=7C
        1F936E=7B 1F9376=7C 1F937E=7B 1F9386=7C 1F939D=7B 1F93A5=7C 1F93AD=7B 1F93B5=7C
        1F93CC=7B 1F93D4=7C 1F93DC=7C 1F93E4=7C 1F93FB=7C 1F9403=7C 1F940B=7C 1F9413=7C
        1F942A=7C 1F9432=7C 1F943A=7C 1F9442=7C 1F9459=7C 1F9461=7C 1F9469=7C 1F9471=7C
        1F9488=7C 1F9490=7C 1F9498=7C 1F94A0=7C 1F94B7=7C 1F94BF=7C 1F94C7=7C 1F94CF=7C
        1F94E6=7C 1F94EE=7C 1F94F6=7C 1F94FE=7C 1F9515=7C 1F951D=7C 1F9525=7C 1F952D=7C
        1F9544=7C 1F954C=7C 1F9554=7C 1F955C=7C 1F9573=7C 1F957B=7C 1F9583=7C 1F958B=7C
        1F95A8=7C 1F95B0=7C 1F95B8=7C 1F95C0=7C 1F95D7=7C 1F95DF=7C 1F95E7=7C 1F95EF=7C
        1F9606=7C 1F960E=7C 1F9616=7C 1F961E=7C 1F9635=7C 1F963D=7C 1F9645=7C 1F964D=7C
        1F9664=7C 1F966C=7C 1F9674=7C 1F967C=7C 1F9693=7C 1F969B=7C 1F96A3=7C 1F96AB=7C
        1F96C2=7C 1F96CA=7C 1F96D2=7C 1F96DA=7C 1F96F1=7C 1F96F9=7C 1F9701=7C 1F9709=7C
        1F9720=7C 1F9728=7C 1F9730=7C 1F9738=7C 1F974F=7C 1F9757=7C 1F975F=7C 1F9767=7C
        1F977E=7C 1F9786=7C 1F978E=7C 1F9796=7C 1F97AD=7C 1F97B5=7C 1F97BD=7C 1F97C5=7C
        1F97DC=7C 1F97E4=7C 1F97EC=7C 1F97F4=7C 1F980B=7C 1F9813=7C 1F981B=7C 1F9823=7C
        1F983A=7C 1F9842=7C 1F984A=7C 1F9852=7C 1F9869=7C 1F9871=7C 1F9879=7C 1F9881=7C
        1F989B=7C 1F98A5=7D 1F98AF=7C 1F98B9=7D 1F98D2=7C 1F98DC=7D 1F98E6=7C 1F98F0=7D
        1F9909=7C 1F9913=7D 1F991D=7C 1F9927=7D 1F9940=7C 1F994A=7D 1F9954=7C 1F995E=7D
        1F9977=7C 1F9981=7D 1F998B=7C 1F9995=7D 1F99AE=7C 1F99B8=7D 1F99C2=7C 1F99CC=7D
        1F99E5=7D 1F99EF=7D 1F99F9=7D 1F9A03=7D 1F9A1C=7D 1F9A26=7D 1F9A30=7D 1F9A3A=7D
        1F9A53=7D 1F9A5D=7D 1F9A67=7D 1F9A71=7D 1F9A8A=7D 1F9A94=7D 1F9A9E=7D 1F9AA8=7D
        1F9AC1=7D 1F9ACB=7D 1F9AD5=7D 1F9ADF=7D 1F9AF8=7D 1F9B02=7D 1F9B0C=7D 1F9B16=7D
        1F9B2F=7D 1F9B39=7D 1F9B43=7D 1F9B4D=7D 1F9B66=7D 1F9B70=7D 1F9B7A=7D 1F9B84=7D
        1F9B9D=7D 1F9BA7=7D 1F9BB1=7D 1F9BBB=7D 1F9BD4=7D 1F9BDE=7D 1F9BE8=7D 1F9BF2=7D
        1F9C11=7D 1F9C1B=7D 1F9C25=7D 1F9C2F=7D 1F9C48=7D 1F9C52=7D 1F9C5C=7D 1F9C66=7D
        1F9C7F=7D 1F9C89=7D 1F9C93=7D 1F9C9D=7D 1F9CB6=7D 1F9CC0=7D 1F9CCA=7D 1F9CD4=7D
        1F9CED=7D 1F9CF7=7D 1F9D01=7D 1F9D0B=7D 1F9D24=7D 1F9D2E=7D 1F9D38=7D 1F9D42=7D
        1F9D5B=7D 1F9D65=7D 1F9D6F=7D 1F9D79=7D 1F9D92=7D 1F9D9C=7D 1F9DA6=7D 1F9DB0=7D
        1F9DC9=7D 1F9DD3=7D 1F9DDD=7D 1F9DE7=7D 1F9E00=7D 1F9E0A=7D 1F9E14=7D 1F9E1E=7D
        1F9E37=7D 1F9E41=7D 1F9E4B=7D 1F9E55=7D 1F9E6E=7D 1F9E78=7D 1F9E82=7D 1F9E8C=7D
        1F9EA5=7D 1F9EAF=7D 1F9EB9=7D 1F9EC3=7D 1F9EDC=7D 1F9EE6=7D 1F9EF0=7D 1F9EFA=7D
        1F9F13=7D 1F9F1D=7D 1F9F27=7D 1F9F31=7D 1F9F4A=7D 1F9F54=7D 1F9F5E=7D 1F9F68=7D
        1F9F75=7C 1F9F7A=7D 1F9F7F=7C 1F9F84=7D 1F9F8E=7C 1F9F93=7D 1F9F98=7C 1F9F9D=7D
        1F9FA7=7C 1F9FAC=7D 1F9FB1=7C 1F9FB6=7D 1F9FC0=7C 1F9FC5=7D 1F9FCA=7C 1F9FCF=7D
        1F9FD9=7C 1F9FDE=7D 1F9FE3=7C 1F9FE8=7D 1F9FF2=7C 1F9FF7=7D 1F9FFC=7C 1FA001=7D
        1FA00B=7D 1FA010=7D 1FA015=7D 1FA01A=7D 1FA024=7D 1FA029=7D 1FA02E=7D 1FA033=7D
        1FA03D=7D 1FA042=7D 1FA047=7D 1FA04C=7D 1FA056=7D 1FA05B=7D 1FA060=7D 1FA065=7D
        1FA06F=7D 1FA074=7D 1FA079=7D 1FA07E=7D 1FA088=7D 1FA08D=7D 1FA092=7D 1FA097=7D
        1FA0A1=7D 1FA0A6=7D 1FA0AB=7D 1FA0B0=7D 1FA0BA=7D 1FA0BF=7D 1FA0C4=7D 1FA0C9=7D
        1FA0D3=7D 1FA0D8=7D 1FA0DD=7D 1FA0E2=7D 1FA0EC=7D 1FA0F1=7D 1FA0F6=7D 1FA0FB=7D
        1FA105=7D 1FA10A=7D 1FA10F=7D 1FA114=7D 1FA11E=7D 1FA123=7D 1FA128=7D 1FA12D=7D
        1FA137=7D 1FA13C=7D 1FA141=7D 1FA146=7D 1FA150=7D 1FA155=7D 1FA15A=7D 1FA15F=7D
        1FA169=7D 1FA16E=7D 1FA173=7D 1FA178=7D 1FA182=7D 1FA187=7D 1FA18C=7D 1FA191=7D
        1FA19B=7D 1FA1A0=7D 1FA1A5=7D 1FA1AA=7D 1FA1B4=7D 1FA1B9=7D 1FA1BE=7D 1FA1C3=7D
        1FA1CD=7D 1FA1D2=7D 1FA1D7=7D 1FA1DC=7D 1FA1E6=7D 1FA1EB=7D 1FA1F0=7D 1FA1F5=7D
        1FA1FF=7D 1FA204=7D 1FA209=7D 1FA20E=7D 1FA218=7D 1FA21D=7D 1FA222=7D 1FA227=7D
        1FA231=7D 1FA236=7D 1FA23B=7D 1FA240=7D 1FA24A=7D 1FA24F=7D 1FA254=7D 1FA259=7D
        1FA263=7D 1FA268=7D 1FA26D=7D 1FA272=7D 1FA27C=7D 1FA281=7D 1FA286=7D 1FA28B=7D
        1FA28F=74 1FA2D6=61 1FA41F=61 1FA436=66 1FA5BE=4C20A6 1FA622=30 1FA62A=61 1FA635=79
        1FA69E=61 1FA6A9=79 1FA712=79 1FA77B=79 1FA7E4=79 1FA84D=79 1FA90A=40 1FA944=79
        1FA9A1=40 1FAA2A=40 1FAAC1=40 1FAAE0=79 1FAAF6=79 1FAB40=40 1FAB89=79 1FAB9F=79
        1FABDE=40 1FABFD=79 1FAC0E=79 1FAC24=79 1FAC6E=40 1FACB8=79 1FAD02=40 1FAD21=79
        1FAD32=79 1FAD48=79 1FAD7C=40 1FADA4=79 1FAE04=40 1FAE50=79 1FAE6F=79 1FAE8C=79
        1FAEF3=79 1FAF70=72 1FAF76=72 1FAF7C=72 1FAF82=72 1FAF88=72 1FAF8E=72 1FAF94=72
        1FAF9A=72 1FAFA5=74 1FAFCE=74 1FB0B9=66 1FB0C8=7B 1FB0CB=66 1FB0D0=66 1FB0DD=66
        1FB0E0=7B 1FB0E6=66 1FB0EB=66 1FB0F5=66 1FB0F8=7C 1FB0FB=66 1FB100=66 1FB110=66
        1FB113=7C 1FB119=66 1FB11E=66 1FB12E=66 1FB131=7B 1FB13A=66 1FB144=66 1FB151=66
        1FB154=7C 1FB15D=66 1FB160=66 1FB16C=66 1FB17B=7C 1FB17E=66 1FB183=66 1FB190=66
        1FB193=7C 1FB199=66 1FB19E=66 1FB1A8=66 1FB1AB=7D 1FB1AE=66 1FB1B3=66 1FB1C3=66
        1FB1C6=7D 1FB1CC=66 1FB1D1=66 1FB1E1=66 1FB1E4=7C 1FB1ED=66 1FB1F7=66 1FB204=66
        1FB207=7D 1FB210=66 1FB213=66 1FB21F=66 1FB236=66 1FB23B=66 1FB243=66 1FB249=66
        1FB24E=66 1FB25E=66 1FB268=66 1FB272=66 1FB277=66 1FB280=66 1FB283=66 1FB28F=66
        1FB2A6=66 1FB2AB=66 1FB2B3=66 1FB2B9=66 1FB2BE=66 1FB2CE=66 1FB2D8=66 1FB2E2=66
        1FB2E7=66 1FB2F0=66 1FB2F3=66
        """;

    /// <summary>From `.resources/sa1/lm-sa1-exanim.smc`: the SA-1 base `--sa1prep` leaves, with one
    /// ExAnimation slot saved into level 105 by LM 3.40 (`Invoke-LmGui.ps1 -Action SaveExAnim`, the
    /// slot exanim_1 has). ApplySa1 left the engine unhooked then, so LM installed its SA-1 engine fresh:
    /// the engine, its two helpers and the seven hooks, against exanim_1's LoROM copies — the bytes
    /// LmExAnimEngine relocated. RAM operands moved ($0Dxx → $6Dxx, $14 → $74, $7FC0xx stays), the
    /// direct page set to $3000, one branch shortened, and the second stub call made `JSL $7FC020`;
    /// the first names our trampoline and is the engine's own SA-1 form. The hooks are LM's LoROM
    /// ones but for their targets. Pinned by Sa1FormsTests.</summary>
    public const string ExAnimSave = """
        1E955C=6D 1E955F=6D 1E9562=6D 1E9567=79 1E958B=6D 1E9593=6D 1E959D=6D 1E95A5=6D
        1E95AF=6D 1E95BC=6D 1E95C3=6D 1E9831=49 1E987D=30 1E98D9=6D 1E98DC=6D 1E98DF=6D
        1E9AB6=74 1E9B0C=69 1E9B15=67 1E9B19=69 1E9B35=69 1E9B44=67 1E9B5B=69 1E9B85=67
        1E9B92=20C07F 1E9BE7=69 1E9BF4=67 1E9C71=69 1E9C7A=67 1E9CA2=74 1E9CDF=67 1E9CE3=69
        1E9CE8=74 1E9CED=74 1E9CF2=74 1E9CF7=74 1E9CFC=6F 1E9D05=74 1E9D76=6F 1E9E5F=74
        1EA042=6D
        """;

    /// <summary>From `.resources/sa1/lm-sa1-ow-exanim.smc`: the same SA-1 base with the same slot
    /// saved onto the main map from LM 3.40's overworld editor (`Invoke-LmGui.ps1 -Action
    /// SaveOwExAnim`); ApplySa1 left the overworld engine unhooked then, so LM installed its SA-1 form
    /// fresh. Against ow_exanim_1.smc, the same save on vanilla: the blob LmOwExAnimEngine relocated,
    /// and its hooks. Pinned by Sa1FormsTests.</summary>
    public const string OwExAnimSave = """
        1EA804=6D 1EA807=7F 1EA9A4=61 1EA9BE=6D 1EAA11=6A 1EAF6C=67 1EAF75=67 1EAF90=67
        1EAFA8=67 1EB025=67 1EB095=67 1EB0E4=67 1EB0F5=7F
        """;

    /// <summary>From `.resources/sa1/lm-sa1-4bpp.smc`: clean + pack, its GFX exported and re-imported
    /// and GFX00 imported as ExGFX100 by LM 3.40's command line, which installs LM's 4bpp mode. Against
    /// gfx4bpp_1.smc, the same on vanilla. Two RAM moves in the filter body ($1Bxx → $7Bxx), and the
    /// 21 overworld sprite-table operands of V13 dropped: on LoROM LM moves the tables from $7EB9xx to
    /// $7FC5xx, past the 4bpp file's overrun of the buffer; the pack has already moved them to
    /// $4188xx, which the overrun never reaches and the SA-1 CPU can read, and LM leaves the pack's
    /// operands standing. Pinned by Sa1FormsTests.</summary>
    public const string FourBppSave = """
        00AB17=7B 00AB2D=7B 04F2B8=.. 04F2BF=.. 04F2C6=.. 04F2CD=.. 04F2D3=.. 04F2D7=..
        04F2E0=.. 04F2E7=.. 04F2ED=.. 04F32D=.. 04F33C=.. 04F340=.. 04F345=.. 04F39F=..
        04F3A8=.. 04F3AC=.. 04F3B0=.. 04F3C1=.. 04F3C5=.. 04F3CB=.. 04F3CF=..
        """;

    /// <summary>From `.resources/sa1/lm-sa1-midway.smc`: clean + pack, level 105 saved with separate
    /// midway settings by LM 3.40 (`Invoke-LmGui.ps1 -Action SaveMidway`), against midway_1.smc, the
    /// same on vanilla. Seven RAM operands in the blob V22 carries: `$192A` → `$792A`, `$13CD` →
    /// `$73CD`, `$141A` → `$741A`. Pinned by Sa1FormsTests.</summary>
    public const string MidwaySave = """
        13C535=79 13C556=73 13C58B=73 13C591=74 13C597=79 13C5B2=79 13C5B8=74
        """;

    // ---------------------------------------------------------------- apply

    /// <summary>Play every diff onto an SA-1 overlay (PC → byte): set the bytes, drop the dropped.</summary>
    public static void Apply(Dictionary<int, byte> overlay)
    {
        foreach (var (snes, bytes, dropped) in Parse(LevelSave).Concat(Parse(ExAnimSave)).Concat(Parse(OwExAnimSave)).Concat(Parse(FourBppSave)).Concat(Parse(MidwaySave)))
        {
            int pc = Rom.SnesToPc(snes);
            if (bytes != null) for (int i = 0; i < bytes.Length; i++) overlay[pc + i] = bytes[i];
            else for (int i = 0; i < dropped; i++) overlay.Remove(pc + i);
        }
    }

    internal static IEnumerable<(int Snes, byte[]? Bytes, int Dropped)> Parse(string diff)
    {
        foreach (var token in diff.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            int snes = Convert.ToInt32(token[..eq], 16);
            string v = token[(eq + 1)..];
            if (v[0] == '.') yield return (snes, null, v.Length);
            else yield return (snes, Convert.FromHexString(v), 0);
        }
    }

    // ---------------------------------------------------------------- generate

    private enum Kind
    {
        Plain,   // the oracle holds it at our address
        Hook,    // a JSL/JML (plus displaced bytes): the target is ours, the rest compared
        Block,   // one of LM's relocatable blocks: found in the oracle by content, pointers skipped
    }

    /// <summary>
    /// The pieces a plain level save installs and how the oracle holds each. Block bases are where
    /// after.smc carries blocks A-F (LmLevelEntry's source copies); the stamp at $0DE1AC is given
    /// LM's SA-1 extent, 0x64 bytes: the third little bank-$0D routine grows from 7 bytes to 23 and
    /// its `LM 01 01` marker moves to $0DE20C, into bytes after.smc leaves free.
    /// </summary>
    private static IEnumerable<(string Name, Kind Kind, int Snes, int Length, int BaseSnes)> Pieces(List<(int Pc, byte[] Bytes)> stamps)
    {
        int Len(int snes) => stamps.Last(s => s.Pc == Rom.SnesToPc(snes)).Bytes.Length;
        yield return ("bank $1F engine", Kind.Plain, LmLevelRender.Bank1FSnes, LmLevelRender.Bank1FSize, 0);
        foreach (var (s, b) in LmLevelRender.Blocks()) yield return ($"block {s:X6}", Kind.Plain, s, b.Length, 0);
        foreach (var (s, b) in LmLevelRender.InPlace()) yield return ($"edit {s:X6}", Kind.Plain, s, b.Length, 0);
        foreach (var (s, b) in LmLevelEntry.Hooks()) yield return ($"hook {s:X6}", Kind.Hook, s, b.Length, 0);
        foreach (var (s, b) in LmLevelEntry.HeightHooks()) yield return ($"hook {s:X6}", Kind.Hook, s, b.Length, 0);
        foreach (var (s, b) in LmLevelEntry.InPlacePatches()) yield return ($"edit {s:X6}", Kind.Plain, s, s == 0x0DE1AC ? 0x64 : b.Length, 0);
        yield return ("block A", Kind.Block, LmLevelEntry.BlockASnes, LmLevelEntry.BlockA().Length, 0x108141);
        yield return ("block B", Kind.Block, LmLevelEntry.BlockBSnes, LmLevelEntry.BlockB().Length, 0x108AD5);
        yield return ("block C", Kind.Block, LmLevelEntry.BlockCSnes, LmLevelEntry.BlockC().Length, 0x108EED);
        yield return ("block D", Kind.Block, LmLevelEntry.BlockDSnes, LmLevelEntry.BlockD().Length, 0x108E9D);
        yield return ("block E", Kind.Block, LmLevelEntry.BlockESnes, LmLevelEntry.BlockE().Length, 0x108EC5);
        yield return ("block F", Kind.Block, LmLevelEntry.BlockFSnes, LmLevelEntry.BlockF().Length, 0x1092AD);
        yield return ("main entrance routine", Kind.Plain, RomPrep.LmMainEntranceRoutine, Len(RomPrep.LmMainEntranceRoutine), 0);
        yield return ("secondary entrance routine", Kind.Plain, RomPrep.LmSecondaryRoutine, Len(RomPrep.LmSecondaryRoutine), 0);
        yield return ("acts-like core", Kind.Plain, RomPrep.LmActsCore, Len(RomPrep.LmActsCore), 0);
        yield return ("Map16 ladder", Kind.Plain, RomPrep.LmMap16LadderSnes, Len(RomPrep.LmMap16LadderSnes), 0);
        // LM's 4bpp-mode install, which a plain save does not make: this oracle shows only which of its
        // bytes are vanilla's under the pack's remap ($00A8A1), to drop; LM's own 4bpp bytes come from
        // the 4bpp oracle (FourBppSave).
        foreach (int s in new[] { 0x00A897, 0x00AB0B, 0x00B895 })
            yield return ($"4bpp edit {s:X6}", Kind.Plain, s, Len(s), 0);
    }

    /// <summary>
    /// What an ExAnimation save installs: the engine and its helpers as LM wrote them into exanim_1
    /// (the copies LmExAnimEngine relocated into bank $1E), and the hooks. The three hooks that are a
    /// bare JSL operand are not listed: every byte of them is ours. The per-level table is data.
    /// </summary>
    private static IEnumerable<(string Name, Kind Kind, int Snes, int Length, int BaseSnes)> ExAnimPieces()
    {
        yield return ("ExAnimation engine", Kind.Block, LmExAnimEngine.EngineSnes, LmExAnimEngine.Engine().Length, LmExAnimEngine.SourceSnes);
        yield return ("ExAnimation MVN helper", Kind.Block, LmExAnimEngine.MvnSnes, LmExAnimEngine.Mvn().Length, LmExAnimEngine.SourceMvnSnes);
        yield return ("ExAnimation clear helper", Kind.Block, LmExAnimEngine.ClearSnes, LmExAnimEngine.Clear().Length, LmExAnimEngine.SourceClearSnes);
        foreach (var (s, b) in LmExAnimEngine.Hooks())
            if (b.Length > 3) yield return ($"ExAnimation hook {s:X6}", Kind.Hook, s, b.Length, 0);
    }

    /// <summary>
    /// What an overworld ExAnimation save installs: the blob, where <paramref name="owExAnim1"/>'s
    /// setup hook says LM put it, and the hooks — the three JSLs and the three frame-counter reads.
    /// The record table and the settings are data.
    /// </summary>
    private static IEnumerable<(string Name, Kind Kind, int Snes, int Length, int BaseSnes)> OwExAnimPieces(Rom owExAnim1)
    {
        int theirs = owExAnim1.ReadValue(0x048087, 3);
        yield return ("overworld ExAnimation engine", Kind.Block, LmOwExAnimEngine.EngineSnes, LmOwExAnimEngine.Size, theirs);
        foreach (var (s, b) in LmOwExAnimEngine.Hooks())
            yield return ($"overworld ExAnimation hook {s:X6}", b.Length > 3 ? Kind.Hook : Kind.Plain, s, b.Length, 0);
    }

    /// <summary>
    /// What a save with separate midway settings installs: the blob, where <paramref name="midway1"/>'s
    /// `$05D9E3` hook says LM put it (V22's copy; the v10 one at $13BC00 is unreferenced), its two
    /// hooks, and the two edits in vanilla's entrance code. The four tables are data.
    /// </summary>
    private static IEnumerable<(string Name, Kind Kind, int Snes, int Length, int BaseSnes)> MidwayPieces(Rom midway1)
    {
        int theirs = midway1.ReadValue(RomPrep.LmMidwayHook + 1, 3);
        yield return ("midway routine", Kind.Block, RomPrep.MidwayRoutineV22Snes, RomPrep.MidwayRoutineBlockSize - 12, theirs);
        yield return ("midway hook", Kind.Hook, RomPrep.LmMidwayHook, 4, 0);
        yield return ("exit arrival hook", Kind.Hook, RomPrep.LmExitArrivalHook, 4, 0);
        yield return ("midway edit 05D9C3", Kind.Plain, 0x05D9C3, 1, 0);
        yield return ("midway store", Kind.Plain, RomPrep.LmMidwayStore, 5, 0);
    }

    /// <summary>
    /// What LM's 4bpp mode installs, which a GFX + ExGFX import makes: every stamp of ours, clipped to
    /// the bytes <paramref name="gfx4bpp"/> changed against <paramref name="vanilla"/>. Only the code
    /// banks: from $088000 on the import writes the GFX files themselves, which are data. A stamp's
    /// bytes the import leaves alone are not LM's to set, and LM's loader and blocks that our own
    /// stand in for are not stamped at its addresses, so neither is a piece.
    /// </summary>
    private static IEnumerable<(string Name, Kind Kind, int Snes, int Length, int BaseSnes)> FourBppPieces(
        List<(int Pc, byte[] Bytes)> stamps, Rom vanilla, Rom gfx4bpp)
    {
        foreach (var (pc, bytes) in stamps)
        {
            if (Rom.PcToSnes(pc) >= 0x088000) continue;
            for (int i = 0; i < bytes.Length;)
            {
                if (gfx4bpp.ReadByte(Rom.PcToSnes(pc + i)) == vanilla.ReadByte(Rom.PcToSnes(pc + i))) { i++; continue; }
                int start = i;
                while (i < bytes.Length && gfx4bpp.ReadByte(Rom.PcToSnes(pc + i)) != vanilla.ReadByte(Rom.PcToSnes(pc + i))) i++;
                // a run that opens on a JSL/JML is a hook: $0093F7's target is our copy of LM's NMI wait
                // ($13C608), and LM's own lands elsewhere on each base, its low byte equal to ours by chance
                var kind = bytes[start] is 0x22 or 0x5C ? Kind.Hook : Kind.Plain;
                yield return ($"4bpp {Rom.PcToSnes(pc + start):X6}", kind, Rom.PcToSnes(pc + start), i - start, 0);
            }
        }
    }

    /// <summary>
    /// The diff for <see cref="LevelSave"/>, from the oracle. <paramref name="after"/> is LM's LoROM
    /// save (after.smc), <paramref name="oracle"/> its SA-1 save, <paramref name="cleanPack"/> the
    /// pack on a clean ROM and <paramref name="packWrote"/> the PCs it wrote there. A block not found
    /// in the oracle goes to <paramref name="report"/>; a pinned diff has none.
    /// </summary>
    public static string Generate(Rom after, Rom oracle, Rom cleanPack, IReadOnlySet<int> packWrote, Sa1Remap remap, List<string> report)
        => Generate(Pieces, after, oracle, cleanPack, packWrote, remap, report);

    /// <summary>The diff for <see cref="ExAnimSave"/>: <paramref name="exanim1"/> is LM's LoROM
    /// ExAnimation install (exanim_1.smc), <paramref name="oracle"/> its SA-1 one.</summary>
    public static string GenerateExAnim(Rom exanim1, Rom oracle, Rom cleanPack, IReadOnlySet<int> packWrote, Sa1Remap remap, List<string> report)
        => Generate(_ => ExAnimPieces(), exanim1, oracle, cleanPack, packWrote, remap, report);

    /// <summary>The diff for <see cref="OwExAnimSave"/>: <paramref name="owExAnim1"/> is LM's LoROM
    /// overworld ExAnimation install (ow_exanim_1.smc), <paramref name="oracle"/> its SA-1 one.</summary>
    public static string GenerateOwExAnim(Rom owExAnim1, Rom oracle, Rom cleanPack, IReadOnlySet<int> packWrote, Sa1Remap remap, List<string> report)
        => Generate(_ => OwExAnimPieces(owExAnim1), owExAnim1, oracle, cleanPack, packWrote, remap, report);

    /// <summary>The diff for <see cref="MidwaySave"/>: <paramref name="midway1"/> is LM's LoROM
    /// midway install (midway_1.smc), <paramref name="oracle"/> its SA-1 one.</summary>
    public static string GenerateMidway(Rom midway1, Rom oracle, Rom cleanPack, IReadOnlySet<int> packWrote, Sa1Remap remap, List<string> report)
        => Generate(_ => MidwayPieces(midway1), midway1, oracle, cleanPack, packWrote, remap, report);

    /// <summary>The diff for <see cref="FourBppSave"/>: <paramref name="gfx4bpp"/> is LM's LoROM 4bpp
    /// install (gfx4bpp_1.smc), <paramref name="oracle"/> its SA-1 one.</summary>
    public static string GenerateFourBpp(Rom vanilla, Rom gfx4bpp, Rom oracle, Rom cleanPack, IReadOnlySet<int> packWrote, Sa1Remap remap, List<string> report)
        => Generate(stamps => FourBppPieces(stamps, vanilla, gfx4bpp), gfx4bpp, oracle, cleanPack, packWrote, remap, report);

    /// <summary>One diff: every piece decided byte by byte, by the three rules. <paramref name="after"/>
    /// is the LoROM save the pieces' LM bytes come from, <paramref name="oracle"/> the SA-1 save;
    /// <paramref name="remap"/> builds our own SA-1 form of the stamps, whose bytes are never LM's to set.</summary>
    private static string Generate(Func<List<(int Pc, byte[] Bytes)>, IEnumerable<(string Name, Kind Kind, int Snes, int Length, int BaseSnes)>> pieces,
                                   Rom after, Rom oracle, Rom cleanPack, IReadOnlySet<int> packWrote, Sa1Remap remap, List<string> report)
    {
        var stamps = RomPrep.BuildStamps(PrepVersion);
        var lorom = new Dictionary<int, byte>();
        foreach (var (pc, bytes) in stamps)
            for (int i = 0; i < bytes.Length; i++) lorom[pc + i] = bytes[i];
        var ours = new Dictionary<int, byte>();
        foreach (var (pc, bytes) in RomPrep.Sa1Stamps(PrepVersion, remap))
            for (int i = 0; i < bytes.Length; i++) ours[pc + i] = bytes[i];

        var set = new SortedDictionary<int, byte?>();    // our pc → byte, or null = dropped
        void Decide(string name, int ourSnes, int baseSnes, int theirsSnes, int i)
        {
            int pc = Rom.SnesToPc(ourSnes + i), theirsPc = Rom.SnesToPc(theirsSnes + i);
            byte b = after.ReadByte(baseSnes + i), t = oracle.ReadByte(theirsSnes + i);
            if (t == b) return;
            byte o = lorom.TryGetValue(pc, out var ov) ? ov : b;          // past a stamp's end: after.smc's free bytes
            if (o != b) return;                                           // ours over LM's: ours stands, in its own SA-1 form
            if (ours.TryGetValue(pc, out var s) && s != o) return;        // LM's byte our SA-1 form re-points: a block of ours
            bool packs = packWrote.Contains(theirsPc) && t == cleanPack.ReadByte(theirsSnes + i);
            set[pc] = packs ? null : t;
        }

        foreach (var (name, kind, snes, len, baseSnes) in pieces(stamps))
        {
            switch (kind)
            {
                case Kind.Plain:
                    for (int i = 0; i < len; i++) Decide(name, snes, snes, snes, i);
                    break;
                case Kind.Hook:
                    for (int i = 0; i < len;)
                    {
                        if (lorom[Rom.SnesToPc(snes + i)] is 0x22 or 0x5C) { i += 4; continue; }
                        Decide(name, snes, snes, snes, i); i++;
                    }
                    break;
                case Kind.Block:
                    int theirs = Locate(oracle, after, baseSnes, len, snes, out int misses);
                    if (theirs < 0 || misses > len / 8) { report.Add($"{name}: not found in the oracle (best match misses {misses} bytes)"); break; }
                    int delta = (theirs - baseSnes) & 0xFFFF;
                    for (int i = 0; i < len; i++)
                    {
                        // a 16-bit word that moved by the block's own displacement is a pointer into it
                        bool pointer = false;
                        foreach (int j in new[] { i - 1, i })
                            if (j >= 0 && j + 1 < len && ((oracle.ReadValue(theirs + j, 2) - after.ReadValue(baseSnes + j, 2)) & 0xFFFF) == delta) pointer = true;
                        if (!pointer) Decide(name, snes, baseSnes, theirs, i);
                    }
                    break;
            }
        }
        return Format(set);
    }

    /// <summary>Where the oracle holds LM's copy of the block after.smc has at <paramref name="baseSnes"/>:
    /// the best content match, with the first 16 bytes as a filter. A 16-bit word that moved by the
    /// candidate's own displacement is a pointer into the block, not a miss (block A has 210 bytes of them).
    /// Our own copy at <paramref name="ourSnes"/> is never the answer: an oracle saved on our SA-1 base
    /// carries it, relocated and in LoROM form, and it would match better than LM's.</summary>
    private static int Locate(Rom oracle, Rom after, int baseSnes, int len, int ourSnes, out int misses)
    {
        var want = after.Data.AsSpan(after.FileOffset(baseSnes), len).ToArray();
        int best = -1; misses = int.MaxValue;
        for (int pc = 0; pc + len <= oracle.ActualRomSize; pc++)
        {
            if (pc == Rom.SnesToPc(ourSnes)) continue;
            int at = pc + oracle.HeaderOffset, quick = 0;
            for (int i = 0; i < 16; i++) if (oracle.Data[at + i] == want[i]) quick++;
            if (quick < 10) continue;
            int delta = (Rom.PcToSnes(pc) - baseSnes) & 0xFFFF, m = 0;
            bool Pointer(int j) => j >= 0 && j + 1 < len && (((oracle.Data[at + j] | oracle.Data[at + j + 1] << 8) - (want[j] | want[j + 1] << 8)) & 0xFFFF) == delta;
            for (int i = 0; i < len && m < misses; i++)
                if (oracle.Data[at + i] != want[i] && !Pointer(i - 1) && !Pointer(i)) m++;
            if (m < misses) { misses = m; best = pc; }
        }
        return best < 0 ? -1 : Rom.PcToSnes(best);
    }

    /// <summary>Runs of consecutive set bytes as one `SNES=HEX` token, of dropped as `SNES=...`,
    /// eight tokens a line.</summary>
    private static string Format(SortedDictionary<int, byte?> set)
    {
        var tokens = new List<string>();
        int? runPc = null; var run = new StringBuilder(); bool runDropped = false;
        void Flush() { if (runPc is { } p) tokens.Add($"{Rom.PcToSnes(p):X6}={run}"); runPc = null; run.Clear(); }
        int lastPc = -2;
        foreach (var (pc, b) in set)
        {
            bool dropped = b == null;
            if (pc != lastPc + 1 || dropped != runDropped) { Flush(); runPc = pc; runDropped = dropped; }
            run.Append(dropped ? "." : b!.Value.ToString("X2"));
            lastPc = pc;
        }
        Flush();
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++) sb.Append(tokens[i]).Append((i + 1) % 8 == 0 || i == tokens.Count - 1 ? '\n' : ' ');
        return sb.ToString();
    }
}
