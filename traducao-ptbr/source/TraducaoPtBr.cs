using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ImGuiNET;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;

// Tradução PT-BR do Castlevania: SOTN (NEODAN / PO.B.R.E., 2008) para o SymphonyRecomp.
// O patch PPF é aplicado na leitura dos setores do disco, então a imagem do jogo nunca é modificada.

public class TraducaoPtBrMod : IMod
{
    private const string ModId = "traducao-ptbr";
    private const int RawSectorSize = 2352;

    private static readonly FieldInfo ImageField =
        typeof(DiscFs).GetField("_image", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private PpfPatch? patch;
    private string status = "Não carregado.";

    public void OnLoad()
    {
        try
        {
            string root = ModLoader.LoadedMods.FirstOrDefault(m => m.Id == ModId)?.SourcePath
                          ?? Path.Combine(ModLoader.Root, ModId);
            string? ppfPath = Directory.Exists(Path.Combine(root, "patch"))
                ? Directory.EnumerateFiles(Path.Combine(root, "patch"), "*.ppf").FirstOrDefault()
                : null;
            if (ppfPath == null)
            {
                status = "Arquivo .ppf não encontrado na pasta patch do mod.";
                Console.Error.WriteLine("[PT-BR] " + status);
                return;
            }

            patch = PpfPatch.Load(ppfPath);
            Console.WriteLine($"[PT-BR] patch carregado: {patch.Description} ({patch.SectorCount} setores)");
            Apply();
        }
        catch (Exception ex)
        {
            status = "Erro: " + ex.Message;
            Console.Error.WriteLine("[PT-BR] " + ex);
        }

        Event.AddListener<VSyncEvent>(OnVSync);
    }

    public void OnUnload()
    {
        Event.RemoveListener<VSyncEvent>(OnVSync);
        var fs = RecompOne.Runtime.Runtime.Cd?.Fs;
        if (fs != null && fs.Image is PatchedDiscImage p)
            ImageField.SetValue(fs, p.Inner);
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped(status);
        ImGui.Spacing();
        ImGui.TextWrapped("Ao ativar ou desativar o mod, reinicie o jogo (Sistema > Hard Reset) para valer em todas as telas.");
        ImGui.Spacing();
        ImGui.TextDisabled("Tradução original:");
        ImGui.SameLine();
        Link("NEODAN / PO.B.R.E., 2008", OriginalUrl);
    }

    private const string OriginalUrl =
        "https://romhackers.org/traducoes/console/playstation/castlevania-symphony-of-the-night-po.b.r.e";

    private static void Link(string text, string url)
    {
        ImGui.TextColored(new System.Numerics.Vector4(0.45f, 0.70f, 1f, 1f), text);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(url);
        }
        if (ImGui.IsItemClicked())
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Console.Error.WriteLine("[PT-BR] não foi possível abrir o link: " + ex.Message); }
        }
    }

    // Após um Hard Reset o recomp abre o disco de novo; reaplica a camada se precisar.
    private void OnVSync(VSyncEvent e)
    {
        if (patch == null) return;
        var fs = RecompOne.Runtime.Runtime.Cd?.Fs;
        if (fs != null && fs.Image is not PatchedDiscImage) Apply();
    }

    private void Apply()
    {
        var fs = RecompOne.Runtime.Runtime.Cd?.Fs;
        if (fs == null || patch == null) { status = "Disco ainda não carregado."; return; }
        if (fs.Image is PatchedDiscImage) return;

        if (!patch.MatchesDisc(fs.Image))
        {
            status = "Disco incompatível: a tradução exige Castlevania: SOTN (USA) SLUS-00067. Patch NÃO aplicado.";
            Console.Error.WriteLine("[PT-BR] " + status);
            patch = null;
            return;
        }

        ImageField.SetValue(fs, new PatchedDiscImage(fs.Image, patch));
        status = "Tradução ativa.";
        Console.WriteLine("[PT-BR] tradução ativa");
    }

    // Camada sobre a imagem do disco que aplica o patch em cada setor lido.
    private sealed class PatchedDiscImage : IDiscImage
    {
        public readonly IDiscImage Inner;
        private readonly PpfPatch patch;

        public PatchedDiscImage(IDiscImage inner, PpfPatch patch) { Inner = inner; this.patch = patch; }

        public string Format => Inner.Format;
        public int FirstTrack => Inner.FirstTrack;
        public int LastTrack => Inner.LastTrack;
        public bool HasTracks => Inner.HasTracks;
        public int LeadoutLba => Inner.LeadoutLba;
        public int DataSectors => Inner.DataSectors;
        public IReadOnlyList<DiscTrack> Tracks => Inner.Tracks;
        public bool TrackStartLba(int track, out int lba) => Inner.TrackStartLba(track, out lba);
        public void Dispose() => Inner.Dispose();

        public byte[] ReadSectorData(int lba, int size)
        {
            byte[] data = Inner.ReadSectorData(lba, size);
            patch.ApplyTo(lba, DataOffset(size), data);
            return data;
        }

        // Mesma regra do recomp para MODE2/2352: de onde começam os bytes devolvidos dentro do setor bruto.
        private static int DataOffset(int size) => size >= 2340 ? 12 : size >= 2329 ? 16 : 24;
    }

    // Leitor de patches PPF 2.0 / 3.0, organizado por setor.
    private sealed class PpfPatch
    {
        public string Description = "";
        private byte[]? blockCheck;
        private readonly Dictionary<int, (byte[] Data, bool[] Mask)> sectors = new();
        public int SectorCount => sectors.Count;

        public static PpfPatch Load(string path)
        {
            byte[] p = File.ReadAllBytes(path);
            string magic = System.Text.Encoding.ASCII.GetString(p, 0, 5);
            var result = new PpfPatch { Description = System.Text.Encoding.ASCII.GetString(p, 6, 50).Trim() };

            int end = p.Length;
            int diz = IndexOf(p, System.Text.Encoding.ASCII.GetBytes("@BEGIN_FILE_ID.DIZ"));
            if (diz > 0) end = diz;

            int pos;
            bool ppf3 = false, undo = false;
            if (magic == "PPF20")
            {
                result.blockCheck = p.AsSpan(60, 1024).ToArray();
                pos = 1084;
            }
            else if (magic == "PPF30")
            {
                ppf3 = true;
                bool hasBlockCheck = p[57] != 0;
                undo = p[58] != 0;
                pos = 60;
                if (hasBlockCheck) { result.blockCheck = p.AsSpan(60, 1024).ToArray(); pos += 1024; }
            }
            else throw new InvalidDataException("formato de patch não suportado: " + magic);

            while (pos + (ppf3 ? 9 : 5) <= end)
            {
                long off = ppf3 ? BitConverter.ToInt64(p, pos) : BitConverter.ToUInt32(p, pos);
                pos += ppf3 ? 8 : 4;
                int n = p[pos++];
                if (pos + n > end) break;
                for (int i = 0; i < n; i++) result.Set(off + i, p[pos + i]);
                pos += n + (undo ? n : 0);
            }
            return result;
        }

        private void Set(long rawOffset, byte value)
        {
            int sector = (int)(rawOffset / RawSectorSize);
            int inSector = (int)(rawOffset % RawSectorSize);
            if (!sectors.TryGetValue(sector, out var s))
            {
                s = (new byte[RawSectorSize], new bool[RawSectorSize]);
                sectors[sector] = s;
            }
            s.Data[inSector] = value;
            s.Mask[inSector] = true;
        }

        public void ApplyTo(int lba, int dataOffset, byte[] data)
        {
            if (!sectors.TryGetValue(lba, out var s)) return;
            int len = Math.Min(data.Length, RawSectorSize - dataOffset);
            for (int i = 0; i < len; i++)
                if (s.Mask[dataOffset + i]) data[i] = s.Data[dataOffset + i];
        }

        // O PPF guarda 1024 bytes da imagem original a partir do offset 0x9320 (setor 16, byte 32 do setor bruto).
        public bool MatchesDisc(IDiscImage image)
        {
            if (blockCheck == null) return true;
            byte[] pvd = image.ReadSectorData(16, 2048); // dados do usuário começam no byte 24
            return pvd.AsSpan(8, 1024).SequenceEqual(blockCheck);
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
                if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
            return -1;
        }
    }
}
