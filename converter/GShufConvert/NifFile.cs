using System.Numerics;
using System.Buffers.Binary;
using System.Text;
using MaterialLib;

namespace GShufConvert;

/// <summary>A shader a model carries inline (no material file name), with the settings needed to rebuild it as a material file.</summary>
public sealed class InlineShader
{
    public required bool Effect { get; init; }            // BSEffectShaderProperty (BGEM) instead of BSLightingShaderProperty (BGSM)
    public required string Key { get; init; }             // diffuse (lighting) or source (effect) texture, relative to Textures
    public required List<string> Textures { get; init; }  // every texture it uses, relative to Textures
    public required Func<BaseMaterialFile> Build { get; init; }  // a material file reproducing the shader
}

/// <summary>Reads the shader blocks of Fallout 4 NIF files (version 20.2.0.7, Bethesda stream 130) per the NifTools nif.xml layout.</summary>
public static class NifFile
{
    private const uint Fo4StreamVersion = 130;

    /// <summary>Every inline shader in the file; shaders that name a BGSM or BGEM file are skipped.</summary>
    public static List<InlineShader> InlineShaders(byte[] data)
    {
        var result = new List<InlineShader>();
        var blocks = Blocks(data, out var strings);
        if (blocks == null) return result;
        // Geometry blocks point at their shader and alpha property; the alpha property carries blending and testing.
        var alphaFor = new Dictionary<int, int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Type is not ("BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape" or "BSDynamicTriShape")) continue;
            var r = new Reader(data, blocks[i].Offset);
            r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();  // name, extra data, controller
            r.Skip(4 + 12 + 36 + 4); r.Int();              // flags, translation, rotation, scale, collision object
            r.Skip(16); r.Int();                           // bounding sphere, skin
            var shader = r.Int();
            var alpha = r.Int();
            if (shader >= 0 && alpha >= 0) alphaFor.TryAdd(shader, alpha);
        }
        for (var i = 0; i < blocks.Count; i++)
        {
            try
            {
                var alpha = alphaFor.TryGetValue(i, out var a) && a < blocks.Count && blocks[a].Type == "NiAlphaProperty" ? ReadAlpha(data, blocks[a].Offset) : ((ushort, byte)?)null;
                var shader = blocks[i].Type switch
                {
                    "BSLightingShaderProperty" => Lighting(data, blocks[i].Offset, blocks, strings, alpha),
                    "BSEffectShaderProperty" => Effect(data, blocks[i].Offset, strings, alpha),
                    _ => null,
                };
                if (shader != null) result.Add(shader);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A block that does not match the documented layout is left alone.
            }
        }
        return result;
    }

    /// <summary>The source texture (relative to Textures, lowercase) of every BSSkyShaderProperty block, which the engine loads by name.</summary>
    public static List<string> SkyTextures(byte[] data)
    {
        var result = new List<string>();
        var blocks = Blocks(data, out _);
        if (blocks == null) return result;
        foreach (var block in blocks.Where(b => b.Type == "BSSkyShaderProperty"))
        {
            try
            {
                var r = new Reader(data, block.Offset);
                r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();  // name, extra data, controller
                r.Skip(8 + 16);                                // shader flags, UV offset and scale
                var texture = Paths.Normalize(r.SizedString(), "textures");
                if (texture.Length > 0) result.Add(texture);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A block that does not match the documented layout is left alone.
            }
        }
        return result;
    }

    /// <summary>A copy of the file with every material name and texture path passed through the map (null keeps a path):
    /// header strings naming a BGSM or BGEM file, BSShaderTextureSet entries and the texture paths of BSEffectShaderProperty
    /// blocks. Block sizes are recomputed. Returns null when the file is not a Fallout 4 NIF.</summary>
    public static byte[]? Rewrite(byte[] data, Func<string, string?> map)
    {
        var newline = Array.IndexOf(data, (byte)'\n');
        if (newline < 0 || newline > 64 || !Encoding.ASCII.GetString(data, 0, newline).StartsWith("Gamebryo File Format", StringComparison.Ordinal)) return null;
        var r = new Reader(data, newline + 1);
        if (r.UInt() != 0x14020007) return null;
        r.Skip(1);
        r.UInt();
        var numBlocks = (int)r.UInt();
        if (r.UInt() != Fo4StreamVersion) return null;
        r.ExportString(); r.ExportString(); r.ExportString(); r.ExportString();
        var numTypes = r.UShort();
        var types = new string[numTypes];
        for (var i = 0; i < numTypes; i++) types[i] = r.SizedString();
        var typeIndex = new ushort[numBlocks];
        for (var i = 0; i < numBlocks; i++) typeIndex[i] = r.UShort();
        var sizesOffset = r.Position;
        var sizes = new int[numBlocks];
        for (var i = 0; i < numBlocks; i++) sizes[i] = (int)r.UInt();
        var numStrings = (int)r.UInt();
        r.UInt();
        var strings = new List<string>(numStrings);
        for (var i = 0; i < numStrings; i++) strings.Add(r.SizedString());
        var groupsStart = r.Position;
        r.Skip(4 * (int)r.UInt());
        var blocksStart = r.Position;

        string Mapped(string path) => path.Length == 0 ? path : map(path) ?? path;
        var newStrings = strings.Select(s => NamesMaterialFile(s) ? Mapped(s) : s).ToList();

        var output = new MemoryStream();
        var w = new BinaryWriter(output, Encoding.Latin1);
        w.Write(data, 0, sizesOffset);
        var sizesPosition = output.Position;
        w.Write(new byte[4 * numBlocks]);
        w.Write((uint)newStrings.Count);
        w.Write((uint)(newStrings.Count == 0 ? 0 : newStrings.Max(s => Encoding.Latin1.GetByteCount(s))));
        foreach (var s in newStrings) WriteSized(w, s);
        w.Write(data, groupsStart, blocksStart - groupsStart);

        var newSizes = new int[numBlocks];
        var offset = blocksStart;
        for (var i = 0; i < numBlocks; i++)
        {
            var type = types[typeIndex[i] & 0x7FFF];
            var start = output.Position;
            if (type == "BSShaderTextureSet")
            {
                var t = new Reader(data, offset);
                var count = (int)t.UInt();
                w.Write((uint)count);
                for (var k = 0; k < count; k++) WriteSized(w, Mapped(t.SizedString()));
                w.Write(data, t.Position, offset + sizes[i] - t.Position);
            }
            else if (type == "BSEffectShaderProperty")
            {
                var e = new Reader(data, offset);
                e.Int(); e.Skip(4 * (int)e.UInt()); e.Int();  // name, extra data, controller
                e.Skip(8 + 16);                                // shader flags, UV offset and scale
                w.Write(data, offset, e.Position - offset);
                WriteSized(w, Mapped(e.SizedString()));        // source texture
                var middle = e.Position;
                e.Skip(4 + 16 + 16 + 8);                       // clamp and lighting bytes, falloff, base color, scale, soft depth
                w.Write(data, middle, e.Position - middle);
                for (var k = 0; k < 4; k++) WriteSized(w, Mapped(e.SizedString()));  // greyscale, environment, normal, environment mask
                w.Write(data, e.Position, offset + sizes[i] - e.Position);
            }
            else
            {
                w.Write(data, offset, sizes[i]);
            }
            newSizes[i] = (int)(output.Position - start);
            offset += sizes[i];
        }
        w.Write(data, offset, data.Length - offset);  // footer: the root list
        w.Flush();
        var bytes = output.ToArray();
        for (var i = 0; i < numBlocks; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)sizesPosition + 4 * i), (uint)newSizes[i]);
        return bytes;
    }

    /// <summary>A copy of the file with every triangle shape scaled by <paramref name="x"/> across and <paramref name="z"/> up,
    /// and each texture coordinate moved from where it sits in the <paramref name="from"/> layout to the same screen position
    /// of the <paramref name="to"/> layout.</summary>
    public static byte[]? Reshape(byte[] data, float x, float z, UvLayout from, UvLayout to)
    {
        if (from.Determinant == 0) return null;
        return Transform(data, p => (p.X * x, p.Y, p.Z * z), Math.Max(x, z), uv =>
        {
            var (s, t) = from.Where(uv.U, uv.V);
            return to.At(s, t);
        });
    }

    /// <summary>How a picture's texture lies on screen, seen from the front standing up: the texture coordinates at its top
    /// left corner and their change across its full width and down its full height.</summary>
    public readonly record struct UvLayout(float OriginU, float OriginV, float AcrossU, float AcrossV, float DownU, float DownV)
    {
        public float Determinant => AcrossU * DownV - DownU * AcrossV;

        /// <summary>The texture coordinates at screen position (s across, t down), both 0 to 1 over the picture.</summary>
        public (float U, float V) At(float s, float t) => (OriginU + s * AcrossU + t * DownU, OriginV + s * AcrossV + t * DownV);

        /// <summary>The screen position (s across, t down) of texture coordinates.</summary>
        public (float S, float T) Where(float u, float v)
        {
            var (du, dv) = (u - OriginU, v - OriginV);
            return ((du * DownV - DownU * dv) / Determinant, (AcrossU * dv - du * AcrossV) / Determinant);
        }

        /// <summary>True when the texture shows as stored: U grows to the right and V grows downward.</summary>
        public bool Upright => AcrossU > 0 && AcrossV == 0 && DownU == 0 && DownV > 0;

        /// <summary>The rectangle of texture coordinates the layout covers.</summary>
        public UvRect Rect
        {
            get
            {
                var (u1, v1) = At(1, 1);
                return new UvRect(Math.Min(OriginU, u1), Math.Min(OriginV, v1), Math.Max(OriginU, u1), Math.Max(OriginV, v1));
            }
        }
    }

    /// <summary>A rectangle of texture coordinates, from (U0, V0) to (U1, V1).</summary>
    public readonly record struct UvRect(float U0, float V0, float U1, float V1)
    {
        public float Width => U1 - U0;
        public float Height => V1 - V0;
    }

    private static byte[]? Transform(byte[] data, Func<(float X, float Y, float Z), (float X, float Y, float Z)> move, float radiusScale, Func<(float U, float V), (float U, float V)>? moveUv = null)
    {
        var blocks = Blocks(data, out _);
        if (blocks == null) return null;
        var bytes = (byte[])data.Clone();
        foreach (var block in blocks.Where(b => b.Type is "BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape"))
        {
            var r = new Reader(bytes, block.Offset);
            r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();  // name, extra data, controller
            r.Skip(4 + 12 + 36 + 4); r.Int();              // flags, translation, rotation, scale, collision object
            var sphere = r.Position;
            r.Skip(16); r.Int(); r.Int(); r.Int();         // bounding sphere, skin, shader, alpha
            var desc = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(r.Position));
            r.Skip(8);
            r.UInt();                                      // triangles
            var vertices = r.UShort();
            r.UInt();                                      // data size
            var start = r.Position;
            var size = (int)(desc & 0xF) * 4;
            var attributes = (ushort)(desc >> 44);
            var fullPrecision = (attributes & 0x400) != 0;
            if ((attributes & 0x1) == 0 || size == 0) continue;
            for (var i = 0; i < vertices; i++)
            {
                var at = start + i * size;
                var width = fullPrecision ? 4 : 2;
                float Read(int axis) => fullPrecision ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + axis * 4)) : (float)BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(at + axis * 2));
                void Write(int axis, float value)
                {
                    if (fullPrecision) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + axis * width), value);
                    else BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(at + axis * width), (Half)value);
                }
                var moved = move((Read(0), Read(1), Read(2)));
                Write(0, moved.X);
                Write(1, moved.Y);
                Write(2, moved.Z);
                if (moveUv != null && (attributes & 0x2) != 0)
                {
                    var uvAt = at + (int)((desc >> 8) & 0xF) * 4;
                    var uv = moveUv(((float)BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(uvAt)), (float)BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(uvAt + 2))));
                    BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(uvAt), (Half)uv.U);
                    BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(uvAt + 2), (Half)uv.V);
                }
            }
            var center = move((BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(sphere)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(sphere + 4)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(sphere + 8))));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(sphere), center.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(sphere + 4), center.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(sphere + 8), center.Z);
            var radius = bytes.AsSpan(sphere + 12, 4);
            BinaryPrimitives.WriteSingleLittleEndian(radius, BinaryPrimitives.ReadSingleLittleEndian(radius) * radiusScale);
        }
        return bytes;
    }

    /// <summary>The picture face of the triangle shapes whose shader property names a material the predicate accepts: the
    /// triangles within 25 degrees of the direction most of their area faces (a canvas without the edges it wraps around, a
    /// magazine cover without the page edges; of the two sides of a double-sided sign, the one that reads the right way round),
    /// and of those the largest texture island (triangles joined by shared vertices; one street name of a sign that shows
    /// several). Returns that island's extents (x, y, z) in each shape's own space, the rectangle of texture coordinates it
    /// uses, its size as seen from the front standing up (width across, height down; texture-mapping derivatives, whatever
    /// way the face is turned) and how the texture lies on it, and the whole face as seen from the front (every island, for a
    /// sign whose lettering lies over a plate); null when no shape names such a material.</summary>
    public static PictureShape? MaterialShape(byte[] data, Func<string, bool> isMaterial)
    {
        var blocks = Blocks(data, out var strings);
        if (blocks == null) return null;
        var shapes = Vertices(data);
        var shapeIndex = 0;
        var chosen = new List<Shape>();
        foreach (var block in blocks.Where(b => b.Type is "BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape"))
        {
            var shape = shapes[shapeIndex++];
            var r = new Reader(data, block.Offset);
            r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();
            r.Skip(4 + 12 + 36 + 4); r.Int();
            r.Skip(16); r.Int();
            var shader = r.Int();
            if (shader < 0 || shader >= blocks.Count || shape.Positions.Count == 0) continue;
            // A lighting shader starts with its shader type, then its name; an effect shader starts with its name.
            var shaderReader = new Reader(data, blocks[shader].Offset);
            if (blocks[shader].Type == "BSLightingShaderProperty") shaderReader.UInt();
            var name = Str(strings, shaderReader.Int());
            if (name != null && isMaterial(name)) chosen.Add(shape);
        }
        if (chosen.Count == 0) return null;

        // Each triangle's normal, area and position derivatives dP/du and dP/dv (world length and direction per unit of U and V).
        var faces = new List<Face>();
        var areaByAxis = new float[6];
        var readableByAxis = new float[6];
        foreach (var shape in chosen)
        {
            foreach (var (a, b, c) in shape.Triangles)
            {
                if (a >= shape.Positions.Count || b >= shape.Positions.Count || c >= shape.Positions.Count) continue;
                var e1 = Position(shape, b) - Position(shape, a);
                var e2 = Position(shape, c) - Position(shape, a);
                var cross = Vector3.Cross(e1, e2);
                var length = cross.Length();
                if (length <= 0) continue;
                var normal = cross / length;
                Vector3? dPdu = null, dPdv = null;
                if (Math.Max(a, Math.Max(b, c)) < shape.Uvs.Count)
                {
                    var (t0, t1, t2) = (shape.Uvs[a], shape.Uvs[b], shape.Uvs[c]);
                    var (du1, dv1, du2, dv2) = (t1.U - t0.U, t1.V - t0.V, t2.U - t0.U, t2.V - t0.V);
                    var det = du1 * dv2 - du2 * dv1;
                    if (MathF.Abs(det) >= 1e-8f)
                    {
                        dPdu = (e1 * dv2 - e2 * dv1) / det;
                        dPdv = (e2 * du1 - e1 * du2) / det;
                    }
                }
                var face = new Face(shape, a, b, c, normal, length / 2, dPdu, dPdv);
                faces.Add(face);
                var axis = AxisOf(normal);
                areaByAxis[axis] += face.Area;
                if (dPdu is { } du && dPdv is { } dv && !Mirrored(normal, du, dv)) readableByAxis[axis] += face.Area;
            }
        }
        // The direction with the most area; a double-sided picture (the opposite side at least 90% as large) shows the side
        // whose texture reads the right way round.
        var best = Array.IndexOf(areaByAxis, areaByAxis.Max());
        var opposite = best ^ 1;
        if (areaByAxis[opposite] >= 0.9f * areaByAxis[best] && readableByAxis[opposite] > readableByAxis[best]) best = opposite;
        var mean = Vector3.Zero;
        foreach (var face in faces.Where(f => AxisOf(f.Normal) == best)) mean += face.Normal * face.Area;
        var faceNormal = mean.Length() > 0 ? Vector3.Normalize(mean) : Vector3.Zero;
        var facing = faces.Where(f => Vector3.Dot(f.Normal, faceNormal) >= MathF.Cos(25f * MathF.PI / 180f)).ToList();

        // Texture islands of the facing triangles: union-find over each shape's vertex indices, largest total area wins.
        var parent = new Dictionary<(Shape, int), (Shape, int)>();
        (Shape, int) Root((Shape, int) key)
        {
            var root = key;
            while (!parent[root].Equals(root)) root = parent[root];
            while (!key.Equals(root))
            {
                var next = parent[key];
                parent[key] = root;
                key = next;
            }
            return root;
        }
        void Join((Shape, int) a, (Shape, int) b)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            var (ra, rb) = (Root(a), Root(b));
            if (!ra.Equals(rb)) parent[ra] = rb;
        }
        foreach (var f in facing)
        {
            Join((f.Shape, f.A), (f.Shape, f.B));
            Join((f.Shape, f.A), (f.Shape, f.C));
        }
        var islands = facing.GroupBy(f => Root((f.Shape, f.A))).OrderByDescending(g => g.Sum(f => f.Area)).ToList();
        var picture = islands.FirstOrDefault()?.ToList() ?? [];

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
        // Shapes without a triangle list of their own count with all their vertices.
        var corners = picture.Count > 0
            ? picture.SelectMany(f => new[] { (f.Shape, f.A), (f.Shape, f.B), (f.Shape, f.C) })
            : chosen.SelectMany(shape => Enumerable.Range(0, shape.Positions.Count).Select(i => (shape, i)));
        foreach (var (shape, index) in corners)
        {
            var p = shape.Positions[index];
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            if (index >= shape.Uvs.Count) continue;
            var uv = shape.Uvs[index];
            minU = Math.Min(minU, uv.U); maxU = Math.Max(maxU, uv.U);
            minV = Math.Min(minV, uv.V); maxV = Math.Max(maxV, uv.V);
        }
        if (minX > maxX) return null;
        var rect = minU <= maxU ? new UvRect(minU, minV, maxU, maxV) : new UvRect(0, 0, 1, 1);

        // The island's derivatives and normal, weighted by area.
        float perU = 0, perV = 0, weight = 0;
        Vector3 sumU = Vector3.Zero, sumV = Vector3.Zero, sumNormal = Vector3.Zero;
        foreach (var f in picture)
        {
            if (f.DPdu is not { } du || f.DPdv is not { } dv) continue;
            perU += du.Length() * f.Area;
            perV += dv.Length() * f.Area;
            sumU += du * f.Area;
            sumV += dv * f.Area;
            sumNormal += f.Normal * f.Area;
            weight += f.Area;
        }
        var layout = new UvLayout(rect.U0, rect.V0, rect.Width, 0, 0, rect.Height);
        var size = (Width: 0f, Height: 0f);
        if (weight > 0)
        {
            var (worldU, worldV) = (perU / weight * rect.Width, perV / weight * rect.Height);
            var normal = sumNormal.Length() > 0 ? Vector3.Normalize(sumNormal) : Vector3.UnitY;
            var (right, down, standing) = ScreenAxes(normal);
            var (uRight, uDown, vRight, vDown) = (Vector3.Dot(sumU, right), Vector3.Dot(sumU, down), Vector3.Dot(sumV, right), Vector3.Dot(sumV, down));
            float acrossU, acrossV, downU, downV;
            if (!standing)
            {
                // A picture lying flat has no up of its own: it shows as stored, mirrored when its mapping mirrors it.
                var mirrored = uRight * vDown - vRight * uDown < 0;
                (acrossU, acrossV, downU, downV) = (mirrored ? -rect.Width : rect.Width, 0, 0, rect.Height);
                size = (worldU, worldV);
            }
            else if (MathF.Abs(uRight) >= MathF.Abs(uDown))
            {
                // U runs across, V runs down.
                (acrossU, acrossV, downU, downV) = (MathF.Sign(uRight) * rect.Width, 0, 0, (vDown >= 0 ? 1 : -1) * rect.Height);
                size = (worldU, worldV);
            }
            else
            {
                // U runs down, V runs across (a texture turned a quarter).
                (acrossU, acrossV, downU, downV) = (0, (vRight >= 0 ? 1 : -1) * rect.Height, MathF.Sign(uDown) * rect.Width, 0);
                size = (worldV, worldU);
            }
            var originU = acrossU < 0 || downU < 0 ? rect.U1 : rect.U0;
            var originV = acrossV < 0 || downV < 0 ? rect.V1 : rect.V0;
            layout = new UvLayout(originU, originV, acrossU, acrossV, downU, downV);
        }
        var (front, frontSize) = FrontView(facing, faceNormal);
        return new PictureShape((maxX - minX, maxY - minY, maxZ - minZ), rect, size, layout, islands.Count, frontSize, front);
    }

    /// <summary>A picture's face as measured from a model: see <see cref="MaterialShape"/>.</summary>
    public sealed record PictureShape((float X, float Y, float Z) Extents, UvRect Uv, (float Width, float Height) Size, UvLayout Layout,
        int Islands, (float Width, float Height) FaceSize, List<FaceTriangle> Face);

    /// <summary>One triangle of a picture's face seen from the front: positions in world units (x to the right, y down, from
    /// the face's top left corner), texture coordinates, and depth toward the viewer.</summary>
    public readonly record struct FaceTriangle((float X, float Y) P0, (float X, float Y) P1, (float X, float Y) P2,
        (float U, float V) T0, (float U, float V) T1, (float U, float V) T2, float Depth);

    // The facing triangles laid out as seen from the front, and the size of the face. A standing face has the world's up; a
    // face lying flat is seen with its texture's V running down (so a mirroring mapping still reads the right way round).
    private static (List<FaceTriangle> Face, (float Width, float Height) Size) FrontView(List<Face> facing, Vector3 normal)
    {
        var (right, down, standing) = ScreenAxes(normal);
        if (!standing)
        {
            var sumV = Vector3.Zero;
            foreach (var f in facing) if (f.DPdv is { } dv) sumV += dv * f.Area;
            var laid = sumV - Vector3.Dot(sumV, normal) * normal;
            if (laid.Length() > 0)
            {
                down = Vector3.Normalize(laid);
                right = Vector3.Cross(normal, down);
            }
        }
        var triangles = new List<FaceTriangle>();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var f in facing)
        {
            if (Math.Max(f.A, Math.Max(f.B, f.C)) >= f.Shape.Uvs.Count) continue;
            (float X, float Y) At(int i) => (Vector3.Dot(Position(f.Shape, i), right), Vector3.Dot(Position(f.Shape, i), down));
            var (p0, p1, p2) = (At(f.A), At(f.B), At(f.C));
            foreach (var p in new[] { p0, p1, p2 })
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            var depth = Vector3.Dot(Position(f.Shape, f.A) + Position(f.Shape, f.B) + Position(f.Shape, f.C), normal) / 3;
            triangles.Add(new FaceTriangle(p0, p1, p2, f.Shape.Uvs[f.A], f.Shape.Uvs[f.B], f.Shape.Uvs[f.C], depth));
        }
        if (triangles.Count == 0) return (triangles, (0, 0));
        var moved = triangles.Select(t => t with { P0 = (t.P0.X - minX, t.P0.Y - minY), P1 = (t.P1.X - minX, t.P1.Y - minY), P2 = (t.P2.X - minX, t.P2.Y - minY) }).ToList();
        return (moved, (maxX - minX, maxY - minY));
    }

    private sealed record Face(Shape Shape, int A, int B, int C, Vector3 Normal, float Area, Vector3? DPdu, Vector3? DPdv);

    // Screen right and down for a face seen from the front: up is the world's Z laid into the face, or its Y for a face lying
    // flat (then standing is false); right is up x normal.
    private static (Vector3 Right, Vector3 Down, bool Standing) ScreenAxes(Vector3 normal)
    {
        var up = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, normal) * normal;
        var standing = up.Length() >= 0.3f;
        if (!standing) up = Vector3.UnitY - Vector3.Dot(Vector3.UnitY, normal) * normal;
        up = Vector3.Normalize(up);
        return (Vector3.Cross(up, normal), -up, standing);
    }

    // True when a triangle's texture reads mirrored seen from its front.
    private static bool Mirrored(Vector3 normal, Vector3 dPdu, Vector3 dPdv)
    {
        var (right, down, _) = ScreenAxes(normal);
        return Vector3.Dot(dPdu, right) * Vector3.Dot(dPdv, down) - Vector3.Dot(dPdv, right) * Vector3.Dot(dPdu, down) < 0;
    }

    private static Vector3 Position(Shape shape, int index) => new(shape.Positions[index].X, shape.Positions[index].Y, shape.Positions[index].Z);

    // The axis direction a unit normal points closest to: 0 +X, 1 -X, 2 +Y, 3 -Y, 4 +Z, 5 -Z.
    private static int AxisOf(Vector3 n)
    {
        var (ax, ay, az) = (Math.Abs(n.X), Math.Abs(n.Y), Math.Abs(n.Z));
        if (ax >= ay && ax >= az) return n.X >= 0 ? 0 : 1;
        if (ay >= az) return n.Y >= 0 ? 2 : 3;
        return n.Z >= 0 ? 4 : 5;
    }

    private sealed record Shape(List<(float X, float Y, float Z)> Positions, List<(float U, float V)> Uvs, List<(int A, int B, int C)> Triangles);

    // The vertex positions, texture coordinates and triangles of every triangle shape, in each shape's own space.
    private static List<Shape> Vertices(byte[] data)
    {
        var result = new List<Shape>();
        var blocks = Blocks(data, out _);
        if (blocks == null) return result;
        foreach (var block in blocks.Where(b => b.Type is "BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape"))
        {
            var r = new Reader(data, block.Offset);
            r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();
            r.Skip(4 + 12 + 36 + 4); r.Int();
            r.Skip(16); r.Int(); r.Int(); r.Int();
            var desc = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(r.Position));
            r.Skip(8);
            var triangles = (int)r.UInt();
            var vertices = r.UShort();
            var dataSize = r.UInt();
            var start = r.Position;
            var size = (int)(desc & 0xF) * 4;
            var attributes = (ushort)(desc >> 44);
            var full = (attributes & 0x400) != 0;
            var uvOffset = (attributes & 0x2) != 0 ? (int)((desc >> 8) & 0xF) * 4 : -1;
            var shape = new Shape([], [], []);
            for (var i = 0; i < vertices && size > 0; i++)
            {
                var at = start + i * size;
                shape.Positions.Add(full
                    ? (BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 4)), BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at + 8)))
                    : ((float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(at)), (float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(at + 2)), (float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(at + 4))));
                if (uvOffset >= 0) shape.Uvs.Add(((float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(at + uvOffset)), (float)BinaryPrimitives.ReadHalfLittleEndian(data.AsSpan(at + uvOffset + 2))));
            }
            // The triangle list follows the vertex data when the shape carries its own (a data size of 0 means none).
            var triangleStart = start + vertices * size;
            if (dataSize > 0 && size > 0 && triangleStart + triangles * 6 <= block.Offset + block.Size)
            {
                for (var t = 0; t < triangles; t++)
                {
                    var at = triangleStart + t * 6;
                    shape.Triangles.Add((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 4))));
                }
            }
            result.Add(shape);
        }
        return result;
    }

    private static void WriteSized(BinaryWriter w, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        w.Write((uint)bytes.Length);
        w.Write(bytes);
    }

    /// <summary>Every material name and texture path the file references, as stored.</summary>
    public static List<string> References(byte[] data)
    {
        var found = new List<string>();
        Rewrite(data, path => { found.Add(path); return null; });
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record Block(string Type, int Offset, int Size);

    private static List<Block>? Blocks(byte[] data, out List<string> strings)
    {
        strings = [];
        var newline = Array.IndexOf(data, (byte)'\n');
        if (newline < 0 || newline > 64 || !Encoding.ASCII.GetString(data, 0, newline).StartsWith("Gamebryo File Format", StringComparison.Ordinal)) return null;
        var r = new Reader(data, newline + 1);
        var version = r.UInt();
        if (version != 0x14020007) return null;
        r.Skip(1);  // endian
        r.UInt();   // user version
        var numBlocks = (int)r.UInt();
        var bsVersion = r.UInt();
        if (bsVersion != Fo4StreamVersion) return null;
        r.ExportString();  // author
        r.ExportString();  // process script (stream versions below 131)
        r.ExportString();  // export script
        r.ExportString();  // max filepath (stream versions 103 and up)
        var numTypes = r.UShort();
        var types = new string[numTypes];
        for (var i = 0; i < numTypes; i++) types[i] = r.SizedString();
        var typeIndex = new ushort[numBlocks];
        for (var i = 0; i < numBlocks; i++) typeIndex[i] = r.UShort();
        var sizes = new int[numBlocks];
        for (var i = 0; i < numBlocks; i++) sizes[i] = (int)r.UInt();
        var numStrings = (int)r.UInt();
        r.UInt();  // longest string
        for (var i = 0; i < numStrings; i++) strings.Add(r.SizedString());
        var numGroups = (int)r.UInt();
        r.Skip(4 * numGroups);
        var blocks = new List<Block>(numBlocks);
        var offset = r.Position;
        for (var i = 0; i < numBlocks; i++)
        {
            blocks.Add(new Block(types[typeIndex[i] & 0x7FFF], offset, sizes[i]));
            offset += sizes[i];
        }
        return blocks;
    }

    private static (ushort Flags, byte Threshold) ReadAlpha(byte[] data, int offset)
    {
        var r = new Reader(data, offset);
        r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();  // name, extra data, controller
        return (r.UShort(), r.Byte());
    }

    private static bool NamesMaterialFile(string? name)
    {
        return name != null && (name.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Str(List<string> strings, int index) => index >= 0 && index < strings.Count ? strings[index] : null;

    private static string Tex(string path) => Paths.Normalize(path, "textures");

    private static InlineShader? Lighting(byte[] data, int offset, List<Block> blocks, List<string> strings, (ushort Flags, byte Threshold)? alpha)
    {
        var r = new Reader(data, offset);
        var shaderType = r.UInt();
        var name = Str(strings, r.Int());
        if (NamesMaterialFile(name)) return null;
        r.Skip(4 * (int)r.UInt()); r.Int();  // extra data, controller
        var f1 = r.UInt();
        var f2 = r.UInt();
        float uOffset = r.Float(), vOffset = r.Float(), uScale = r.Float(), vScale = r.Float();
        var textureSet = r.Int();
        float er = r.Float(), eg = r.Float(), eb = r.Float();
        var emissiveMult = r.Float();
        var rootMaterial = Str(strings, r.Int()) ?? "";
        var clamp = r.UInt();
        var alphaValue = r.Float();
        var refraction = r.Float();
        var smoothness = r.Float();
        float sr = r.Float(), sg = r.Float(), sb = r.Float();
        var specularStrength = r.Float();
        var subsurfaceRolloff = r.Float();
        var rimPower = r.Float();
        var backlightPower = rimPower >= float.MaxValue && !float.IsPositiveInfinity(rimPower) ? r.Float() : 0f;
        var grayscaleScale = r.Float();
        var fresnel = r.Float();
        float wetSpecScale = r.Float(), wetSpecPower = r.Float(), wetMinVar = r.Float(), wetEnvScale = r.Float(), wetFresnel = r.Float(), wetMetalness = r.Float();
        float envScale = 1f;
        bool ssr = false, wetSsr = false;
        if (shaderType == 1)
        {
            envScale = r.Float();
            ssr = r.Byte() != 0;
            wetSsr = r.Byte() != 0;
        }
        if (textureSet < 0 || textureSet >= blocks.Count || blocks[textureSet].Type != "BSShaderTextureSet") return null;
        var t = new Reader(data, blocks[textureSet].Offset);
        var count = (int)t.UInt();
        var slots = new string[count];
        for (var i = 0; i < count; i++) slots[i] = t.SizedString();
        string At(int i) => i < slots.Length ? Tex(slots[i]) : "";
        if (At(0).Length == 0) return null;

        BaseMaterialFile Build()
        {
            var m = new BGSM
            {
                // Texture set order for Fallout 4: 0 diffuse, 1 normal, 2 glow, 3 greyscale, 4 environment, 5 wrinkles, 6 inner layer, 7 smooth/spec.
                DiffuseTexture = At(0), NormalTexture = At(1), GlowTexture = At(2), GreyscaleTexture = At(3), EnvmapTexture = At(4),
                WrinklesTexture = At(5), InnerLayerTexture = At(6), SmoothSpecTexture = At(7),
                SpecularEnabled = Bit(f1, 0), SpecularColor = Rgb(sr, sg, sb), SpecularMult = specularStrength, Smoothness = smoothness, FresnelPower = fresnel,
                GrayscaleToPaletteScale = grayscaleScale, SubsurfaceLightingRolloff = subsurfaceRolloff,
                RimLighting = rimPower < float.MaxValue, RimPower = rimPower < float.MaxValue ? rimPower : 2f, BackLightPower = backlightPower,
                WetnessControlSpecScale = wetSpecScale, WetnessControlSpecPowerScale = wetSpecPower, WetnessControlSpecMinvar = wetMinVar,
                WetnessControlEnvMapScale = wetEnvScale, WetnessControlFresnelPower = wetFresnel, WetnessControlMetalness = wetMetalness,
                RootMaterialPath = rootMaterial,
                EmitEnabled = Bit(f1, 22), EmittanceColor = Rgb(er, eg, eb), EmittanceMult = emissiveMult,
                ModelSpaceNormals = Bit(f1, 12), ExternalEmittance = Bit(f1, 29), CastShadows = Bit(f1, 9), ReceiveShadows = true,
                HideSecret = Bit(f1, 20), DissolveFade = Bit(f1, 19), Glowmap = Bit(f2, 6), EnvironmentMappingEye = Bit(f1, 17),
                Hair = Bit(f1, 18), Tree = Bit(f2, 29), Facegen = Bit(f1, 10), SkinTint = Bit(f1, 21), Tessellate = Bit(f1, 25),
                AnisoLighting = Bit(f2, 21), SkewSpecularAlpha = Bit(f2, 22),
            };
            Common(m, f1, f2, clamp, uOffset, vOffset, uScale, vScale, alphaValue, alpha);
            m.Refraction = Bit(f1, 15);
            m.RefractionPower = refraction;
            m.EnvironmentMapping = Bit(f1, 7);
            m.EnvironmentMappingMaskScale = envScale;
            m.ScreenSpaceReflections = ssr;
            m.WetnessControlScreenSpaceReflections = wetSsr;
            m.GrayscaleToPaletteColor = Bit(f1, 4);
            return m;
        }

        return new InlineShader { Effect = false, Key = At(0), Textures = slots.Select(Tex).Where(s => s.Length > 0).Distinct().ToList(), Build = Build };
    }

    private static InlineShader? Effect(byte[] data, int offset, List<string> strings, (ushort Flags, byte Threshold)? alpha)
    {
        var r = new Reader(data, offset);
        var name = Str(strings, r.Int());
        if (NamesMaterialFile(name)) return null;
        r.Skip(4 * (int)r.UInt()); r.Int();  // extra data, controller
        var f1 = r.UInt();
        var f2 = r.UInt();
        float uOffset = r.Float(), vOffset = r.Float(), uScale = r.Float(), vScale = r.Float();
        var source = Tex(r.SizedString());
        var clamp = r.Byte();
        var lightingInfluence = r.Byte();
        var envMinLod = r.Byte();
        r.Byte();
        float fStart = r.Float(), fStop = r.Float(), fStartOpacity = r.Float(), fStopOpacity = r.Float();
        float br = r.Float(), bg = r.Float(), bb = r.Float(), ba = r.Float();
        var baseScale = r.Float();
        var softDepth = r.Float();
        var greyscale = Tex(r.SizedString());
        var envmap = Tex(r.SizedString());
        var normal = Tex(r.SizedString());
        var envMask = Tex(r.SizedString());
        var envScale = r.Float();
        if (source.Length == 0) return null;

        BaseMaterialFile Build()
        {
            var m = new BGEM
            {
                BaseTexture = source, GrayscaleTexture = greyscale, EnvmapTexture = envmap, NormalTexture = normal, EnvmapMaskTexture = envMask,
                BaseColor = Rgb(br, bg, bb), BaseColorScale = baseScale, FalloffStartAngle = fStart, FalloffStopAngle = fStop,
                FalloffStartOpacity = fStartOpacity, FalloffStopOpacity = fStopOpacity, LightingInfluence = lightingInfluence / 255f,
                EnvmapMinLOD = envMinLod, SoftDepth = softDepth, FalloffEnabled = Bit(f1, 6), FalloffColorEnabled = Bit(f1, 8),
                GrayscaleToPaletteAlpha = Bit(f1, 5), SoftEnabled = Bit(f1, 30), EffectLightingEnabled = Bit(f2, 30), Glowmap = Bit(f2, 6),
            };
            Common(m, f1, f2, clamp, uOffset, vOffset, uScale, vScale, ba, alpha);
            m.EnvironmentMapping = Bit(f1, 7);
            m.EnvironmentMappingMaskScale = envScale;
            m.GrayscaleToPaletteColor = Bit(f1, 4);
            return m;
        }

        var textures = new[] { source, greyscale, envmap, normal, envMask }.Where(s => s.Length > 0).Distinct().ToList();
        return new InlineShader { Effect = true, Key = source, Textures = textures, Build = Build };
    }

    // Settings both material kinds share: tiling, UV transform, alpha, depth buffer, sidedness, decals.
    private static void Common(BaseMaterialFile m, uint f1, uint f2, uint clamp, float uOffset, float vOffset, float uScale, float vScale, float alphaValue, (ushort Flags, byte Threshold)? alpha)
    {
        // Clamp modes: 0 clamp both, 1 clamp U wrap V, 2 wrap U clamp V, 3 wrap both.
        m.TileU = clamp is 2 or 3;
        m.TileV = clamp is 1 or 3;
        m.UOffset = uOffset;
        m.VOffset = vOffset;
        m.UScale = uScale;
        m.VScale = vScale;
        m.Alpha = alphaValue;
        m.ZBufferWrite = Bit(f2, 0);
        m.ZBufferTest = Bit(f1, 31);
        m.TwoSided = Bit(f2, 4);
        m.Decal = Bit(f1, 26);
        m.DecalNoFade = Bit(f1, 26) && Bit(f2, 3);
        m.AlphaBlendMode = BaseMaterialFile.AlphaBlendModeType.Unknown;
        if (alpha is { } a)
        {
            var blend = (a.Flags & 0x1) != 0;
            var source = (a.Flags >> 1) & 0xF;
            var destination = (a.Flags >> 5) & 0xF;
            m.AlphaBlendMode = !blend ? (source == 6 && destination == 7 ? BaseMaterialFile.AlphaBlendModeType.Unknown : BaseMaterialFile.AlphaBlendModeType.None)
                : source == 6 && destination == 0 ? BaseMaterialFile.AlphaBlendModeType.Additive
                : source == 4 && destination == 1 ? BaseMaterialFile.AlphaBlendModeType.Multiplicative
                : BaseMaterialFile.AlphaBlendModeType.Standard;
            m.AlphaTest = (a.Flags & 0x200) != 0;
            m.AlphaTestRef = a.Threshold;
        }
    }

    private static bool Bit(uint flags, int bit) => (flags & (1u << bit)) != 0;

    private static uint Rgb(float r, float g, float b)
    {
        static uint C(float v) => (uint)Math.Clamp((int)(v * 255f), 0, 255);
        return (C(r) << 16) | (C(g) << 8) | C(b);
    }

    private sealed class Reader(byte[] data, int position)
    {
        public int Position { get; private set; } = position;

        public void Skip(int count) => Position += count;
        public byte Byte() => data[Position++];

        public ushort UShort()
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position));
            Position += 2;
            return v;
        }

        public uint UInt()
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position));
            Position += 4;
            return v;
        }

        public int Int() => (int)UInt();
        public float Float() => BitConverter.Int32BitsToSingle(Int());

        public string SizedString()
        {
            var length = (int)UInt();
            var text = Encoding.Latin1.GetString(data, Position, length);
            Position += length;
            return text;
        }

        public string ExportString()
        {
            var length = Byte();
            var text = Encoding.Latin1.GetString(data, Position, length).TrimEnd('\0');
            Position += length;
            return text;
        }
    }
}
