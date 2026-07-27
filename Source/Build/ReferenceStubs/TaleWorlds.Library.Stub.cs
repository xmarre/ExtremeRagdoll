using System;
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]

namespace TaleWorlds.Library
{
    public struct Mat3
    {
        public Vec3 s;
        public Vec3 f;
        public Vec3 u;
    }

    public struct MatrixFrame
    {
        public Mat3 rotation;
        public Vec3 origin;
    }

    public struct Vec2
    {
        public float x;
        public float y;
    }

    public struct Vec3
    {
        public static readonly Vec3 Zero;

        public float x;
        public float y;
        public float z;
        public float w;

        public Vec3(float x, float y, float z, float w = -1f)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public float LengthSquared { get { return x * x + y * y + z * z; } }

        public Vec3 NormalizedCopy()
        {
            float length = (float)Math.Sqrt(LengthSquared);
            return length <= 0f ? Zero : this * (1f / length);
        }

        public static Vec3 operator +(Vec3 left, Vec3 right)
        {
            return new Vec3(left.x + right.x, left.y + right.y, left.z + right.z);
        }

        public static Vec3 operator -(Vec3 left, Vec3 right)
        {
            return new Vec3(left.x - right.x, left.y - right.y, left.z - right.z);
        }

        public static Vec3 operator *(Vec3 value, float scale)
        {
            return new Vec3(value.x * scale, value.y * scale, value.z * scale);
        }
    }
}
