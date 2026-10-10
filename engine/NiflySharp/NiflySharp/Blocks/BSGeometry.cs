using NiflySharp.Structs;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace NiflySharp.Blocks
{
    // TODO
    public partial class BSGeometry : INiShape
    {
        /// <summary>
        /// No separate geometry data on BSGeometry.
        /// </summary>
        public NiGeometryData GeometryData { get => null; set { } }

        public bool HasData => false;
        public NiBlockRef<NiGeometryData> DataRef { get => null; set { } }

        public bool HasSkinInstance => !SkinInstanceRef?.IsEmpty() ?? false;
        INiRef INiShape.SkinInstanceRef { get => _skin; }
        public NiBlockRef<NiObject> SkinInstanceRef { get => _skin; set => _skin = value; }

        public bool HasShaderProperty => !ShaderPropertyRef?.IsEmpty() ?? false;
        INiRef INiShape.ShaderPropertyRef { get => _shaderProperty; }
        public NiBlockRef<BSShaderProperty> ShaderPropertyRef { get => _shaderProperty; set => _shaderProperty = value; }

        public bool HasAlphaProperty => !AlphaPropertyRef?.IsEmpty() ?? false;
        public NiBlockRef<NiAlphaProperty> AlphaPropertyRef { get => _alphaProperty; set => _alphaProperty = value; }

        public BoundingSphere Bounds
        {
            get => new(_boundingSphere.Center, _boundingSphere.Radius);
            set => _boundingSphere = new NiBound() { Center = value.Center, Radius = value.Radius };
        }

        public BSGeometry()
        {
            _boundMinMax = new float[6];
        }

        public BSGeometry(NiVersion version, List<Vector3> vertices, List<Triangle> triangles, List<TexCoord> uvSets, List<Vector3> normals) : this()
        {
            Create(version, vertices, triangles, uvSets, normals);
        }

        public void UpdateBounds()
        {
            throw new NotImplementedException();
        }

        public bool HasVertices
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public bool HasUVs
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public bool HasSecondUVs
        {
            get
            {
                return false;
            }
            set
            {
            }
        }

        public bool HasNormals
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public bool HasTangents
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public bool HasVertexColors
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public bool IsSkinned
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public bool HasEyeData
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public bool CanChangePrecision
        {
            get
            {
                return false;
            }
        }

        public bool IsFullPrecision
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public ushort VertexCount
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public int TriangleCount => throw new NotImplementedException();

        public List<Triangle> Triangles => throw new NotImplementedException();

        public void SetTriangles(NiVersion version, List<Triangle> triangles)
        {
            throw new NotImplementedException();
        }

        public void Create(NiVersion version, List<Vector3> vertices, List<Triangle> triangles, List<TexCoord> uvSets, List<Vector3> normals)
        {
            throw new NotImplementedException();
        }
    }
}
