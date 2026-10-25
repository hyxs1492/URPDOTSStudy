using Unity.Mathematics;

public struct AABB
{
    public float3 Center;
    public float3 Extents;

    public float3 Size
    {
        get => Extents * 2f;
        set => Extents = value * 0.5f;
    }

    public float3 Min
    {
        get => Center - Extents;
        set => SetMinMax(value, Max);
    }

    public float3 Max
    {
        get => Center + Extents;
        set => SetMinMax(Min, value);
    }

    public AABB(float3 center, float3 size)
    {
        Center = center;
        Extents = size * 0.5f;
    }

    public void SetMinMax(float3 min, float3 max)
    {
        Extents = (max - min) * 0.5f;
        Center = min + Extents;
    }

    public void Encapsulate(float3 point)
    {
        SetMinMax(math.min(Min, point), math.max(Max, point));
    }

    public void Encapsulate(AABB bounds)
    {
        Encapsulate(bounds.Center - bounds.Extents);
        Encapsulate(bounds.Center + bounds.Extents);
    }

    public void Expand(float amount)
    {
        amount *= 0.5f;
        Extents += new float3(amount, amount, amount);
    }

    public void Expand(float3 amount)
    {
        Extents += amount * 0.5f;
    }

    public bool Intersects(AABB bounds)
    {
        return Min.x <= bounds.Max.x && Max.x >= bounds.Min.x && Min.y <= bounds.Max.y && Max.y >= bounds.Min.y && Min.z <= bounds.Max.z && Max.z >= bounds.Min.z;
    }

    public bool Contains(float3 point)
    {
        return point.x >= Min.x && point.x <= Max.x &&
               point.y >= Min.y && point.y <= Max.y &&
               point.z >= Min.z && point.z <= Max.z;
    }

    public override int GetHashCode()
    {
        return Center.GetHashCode() ^ (Extents.GetHashCode() << 2);
    }

    public override bool Equals(object other)
    {
        if (!(other is AABB))
        {
            return false;
        }

        return Equals((AABB)other);
    }

    public bool Equals(AABB other)
    {
        return Center.Equals(other.Center) && Extents.Equals(other.Extents);
    }

    /// <summary>
    /// 从平面的 <see cref="AABB2D"/> 隐式转换而来：xz 平面 → 三维盒（y 给一层薄片）。
    /// </summary>
    public static implicit operator AABB(AABB2D rect)
        => new AABB
        {
            Center = new float3(rect.Center.x, 0f, rect.Center.y),
            Extents = new float3(rect.Extents.x, 1f, rect.Extents.y),
        };

    public static bool operator ==(AABB lhs, AABB rhs)
    {
        return lhs.Center.Equals(rhs.Center) && lhs.Extents.Equals(rhs.Extents);
    }

    public static bool operator !=(AABB lhs, AABB rhs)
    {
        return !(lhs == rhs);
    }
}
