using UnityEngine;
using UnityEditor;

public static class TextureGenerator
{
    [MenuItem("Tools/Generate Radial Glow")]
    static void Generate()
    {
        Texture2D tex = CreateRadialGlow(128);   // 复用上面的函数
        byte[] png = tex.EncodeToPNG();
        System.IO.File.WriteAllBytes("Assets/Textures/RadialGlow.png", png);
        AssetDatabase.Refresh();
    }
    static Texture2D CreateRadialGlow(int size = 128)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);

        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size - 0.5f;
                float dy = (y + 0.5f) / size - 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f; // 归一化 0~1，1 在边缘

                float falloff = Mathf.Clamp01(1f - d);
                //falloff = falloff * falloff;                  // 二次衰减：中心更集中，光更"尖"

                pixels[y * size + x] = new Color(1f, 1f, 1f, falloff);
            }
        }

      tex.SetPixels(pixels);
      tex.Apply();
      tex.wrapMode = TextureWrapMode.Clamp;
      return tex;
  }
}