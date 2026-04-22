using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Renders an overlay color around a specified 'Hole' RectTransform.
/// The center area becomes transparent and allows click-through events.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class HollowOverlayImage : MaskableGraphic, ICanvasRaycastFilter
{
    [Header("Target Settings")]
    [Tooltip("The RectTransform that defines the hole area.")]
    [SerializeField]
    private RectTransform holeTarget;

    // Cache for performance to avoid allocations
    private readonly Vector3[] _fourCorners = new Vector3[4];

    /// <summary>
    /// Forces the geometry to rebuild when the hole moves or resizes.
    /// </summary>
    private void Update()
    {
        // In a production environment, use event-driven updates instead of checking every frame if possible.
        // For simple UI, this ensures the hole stays perfectly synced with animations.
        if (holeTarget != null && holeTarget.hasChanged)
        {
            SetVerticesDirty();
            holeTarget.hasChanged = false;
        }
    }

    /// <summary>
    /// Generates the mesh for the overlay, excluding the hole area.
    /// </summary>
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        if (holeTarget == null)
        {
            base.OnPopulateMesh(vh); // Draw full rectangle if no hole
            return;
        }

        vh.Clear();

        // 1. Get Bounds of the overlay (Self)
        Rect outer = GetPixelAdjustedRect();

        // 2. Get Bounds of the hole (Target) converted to local space
        holeTarget.GetWorldCorners(_fourCorners);

        // Convert world corners to local corners of this overlay
        // Bottom-Left
        var innerMin = rectTransform.InverseTransformPoint(_fourCorners[0]);
        // Top-Right
        var innerMax = rectTransform.InverseTransformPoint(_fourCorners[2]);

        // 3. Draw 4 Quads around the hole (Top, Bottom, Left, Right)
        // Note: UVs are set to (0,0) for simple color fill. If using a texture, UV mapping logic is needed.

        var color32 = (Color32)color;

        // Top Block
        AddQuad(vh,
            new Vector2(outer.xMin, innerMax.y),
            new Vector2(outer.xMax, outer.yMax),
            color32);

        // Bottom Block
        AddQuad(vh,
            new Vector2(outer.xMin, outer.yMin),
            new Vector2(outer.xMax, innerMin.y),
            color32);

        // Left Block (Center vertical)
        AddQuad(vh,
            new Vector2(outer.xMin, innerMin.y),
            new Vector2(innerMin.x, innerMax.y),
            color32);

        // Right Block (Center vertical)
        AddQuad(vh,
            new Vector2(innerMax.x, innerMin.y),
            new Vector2(outer.xMax, innerMax.y),
            color32);
    }

    private void AddQuad(VertexHelper vh, Vector2 min, Vector2 max, Color32 color)
    {
        var startIndex = vh.currentVertCount;

        vh.AddVert(new Vector3(min.x, min.y), color, Vector2.zero);
        vh.AddVert(new Vector3(min.x, max.y), color, Vector2.zero);
        vh.AddVert(new Vector3(max.x, max.y), color, Vector2.zero);
        vh.AddVert(new Vector3(max.x, min.y), color, Vector2.zero);

        vh.AddTriangle(startIndex, startIndex + 1, startIndex + 2);
        vh.AddTriangle(startIndex + 2, startIndex + 3, startIndex);
    }

    /// <summary>
    /// Checks if the raycast position is INSIDE the hole. 
    /// If inside the hole, return false (pass through). If on the overlay, return true (block).
    /// </summary>
    public bool IsRaycastLocationValid(Vector2 sp, Camera eventCamera)
    {
        if (holeTarget == null || !isActiveAndEnabled)
            return true;

        // Check if the screen point is inside the hole's rectangle
        var isInsideHole = RectTransformUtility.RectangleContainsScreenPoint(holeTarget, sp, eventCamera);

        // If it's inside the hole, the raycast is INVALID for this object (it passes through)
        return !isInsideHole;
    }
}