using Unity.Mathematics;


/// <summary>
/// 16个字节
/// </summary>
public struct AABB2D
{
    public float2 Center;
    public float2 Extents;

    public float2 Size
    {
        get => Extents * 2f;
        set => Extents = value * 0.5f;
    }

    public float Width
    {
        get => Size.x;
        set => Size = new float2(value, Size.y);
    }

    public float Height
    {
        get => Size.y;
        set => Size = new float2(Size.x, value);
    }

    public float2 Min
    {
        get => Center - Extents;
        set => SetMinMax(value, Max);
    }

    public float2 Max
    {
        get => Center + Extents;
        set => SetMinMax(Min, value);
    }

    public AABB2D(float2 center, float2 size)
    {
        Center = center;
        Extents = size * 0.5f;
    }

    public AABB2D(float2 center, float radius)
    {
        Center = center;
        Extents = new float2(radius, radius);
    }
    public AABB2D(float2 bottomCenter, float height, float width)
    {
        Center = bottomCenter + height * 0.5f;
        Extents = new float2(height, width); ;
    }

    public AABB2D(float2 bottomCenter, float2 direction, float height, float width)
    {
        float halfLength = height * 0.5f;
        float halfWidth = width * 0.5f;
        Center = bottomCenter + direction * halfLength;
        Extents = new float2(halfWidth, halfLength);
    }


    public void SetMinMax(float2 min, float2 max)
    {
        Extents = (max - min) * 0.5f;
        Center = min + Extents;
    }

    public void Encapsulate(float2 point)
    {
        SetMinMax(math.min(Min, point), math.max(Max, point));
    }

    public void Encapsulate(AABB2D bounds)
    {
        Encapsulate(bounds.Center - bounds.Extents);
        Encapsulate(bounds.Center + bounds.Extents);
    }

    public void Expand(float amount)
    {
        amount *= 0.5f;
        Extents += new float2(amount, amount);
    }

    public void Expand(float2 amount)
    {
        Extents += amount * 0.5f;
    }

    public bool Intersects(AABB2D bounds)
    {
        return Min.x <= bounds.Max.x && Max.x >= bounds.Min.x && Min.y <= bounds.Max.y && Max.y >= bounds.Min.y;
    }

    public bool Contains(float2 point)
    {
        return point.x >= Min.x && point.x <= Max.x &&
               point.y >= Min.y && point.y <= Max.y;
    }

    public bool Contains(AABB2D b)
    {
        return Contains(b.Center + new float2(-b.Extents.x, -b.Extents.y)) &&
               Contains(b.Center + new float2(-b.Extents.x, b.Extents.y)) &&
               Contains(b.Center + new float2(b.Extents.x, -b.Extents.y)) &&
               Contains(b.Center + new float2(b.Extents.x, b.Extents.y));
    }

    public float2 GetClosetPoint(float2 point)
    {
        point.x = math.clamp(point.x, Min.x, Max.x);
        point.y = math.clamp(point.y, Min.y, Max.y);
        return point;
    }

    public override int GetHashCode()
    {
        return Center.GetHashCode() ^ (Extents.GetHashCode() << 2);
    }

    public override bool Equals(object other)
    {
        if (!(other is AABB2D))
        {
            return false;
        }

        return Equals((AABB2D)other);
    }

    public bool Equals(AABB2D other)
    {
        return Center.Equals(other.Center) && Extents.Equals(other.Extents);
    }

    public static bool operator ==(AABB2D lhs, AABB2D rhs)
    {
        return lhs.Center.Equals(rhs.Center) && lhs.Extents.Equals(rhs.Extents);
    }

    public static bool operator !=(AABB2D lhs, AABB2D rhs)
    {
        return !(lhs == rhs);
    }
}
