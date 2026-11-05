using UnityEngine;

// 토성 고리 메시: 가로가 반지름 방향인 띠 텍스처(왼쪽 = 안쪽, 오른쪽 = 바깥쪽)를 원판 모양으로 감음
// Saturn(기본 Sphere, 반지름 0.5) 의 자식에 붙이면 토성의 자전축 기울기를 그대로 따라감
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SaturnRing : MonoBehaviour
{
    // 토성 적도반지름(60,268 km) 기준 배수. C고리 안쪽 74,658 km ~ A고리 바깥 136,775 km
    public float innerRadius = 1.239f;
    public float outerRadius = 2.270f;
    public int segments = 128;

    Mesh mesh;
    bool dirty;

    void OnEnable() { Build(); }
    void OnValidate() { dirty = true; }      // Inspector 값 바꾸면 다음 Update에서 다시 생성
    void Update() { if (dirty) Build(); }

    void Build()
    {
        // 앞/뒷면을 따로 만들어서 위아래 어디서 봐도 보이게 (Render Face 설정 불필요)
        int n = segments + 1;
        Vector3[] v = new Vector3[n * 4];
        Vector3[] nor = new Vector3[n * 4];
        Vector2[] uv = new Vector2[n * 4];
        int[] tri = new int[segments * 12];

        // 기본 Sphere 반지름이 0.5라서 반지름 배수에 0.5를 곱함
        float rin = innerRadius * 0.5f, rout = outerRadius * 0.5f;
        for (int i = 0; i < n; i++)
        {
            float a = (float)i / segments * Mathf.PI * 2f;
            Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            for (int side = 0; side < 2; side++)
            {
                int k = side * n * 2 + i * 2;
                v[k] = dir * rin;      uv[k] = new Vector2(0f, 0.5f);
                v[k + 1] = dir * rout; uv[k + 1] = new Vector2(1f, 0.5f);
                nor[k] = nor[k + 1] = side == 0 ? Vector3.up : Vector3.down;
            }
        }

        int t = 0;
        for (int i = 0; i < segments; i++)
        {
            int a = i * 2, b = a + 2;                 // 윗면
            tri[t++] = a; tri[t++] = b; tri[t++] = a + 1;
            tri[t++] = a + 1; tri[t++] = b; tri[t++] = b + 1;

            int o = n * 2;                            // 아랫면 (감는 방향 반대)
            tri[t++] = o + a; tri[t++] = o + a + 1; tri[t++] = o + b;
            tri[t++] = o + a + 1; tri[t++] = o + b + 1; tri[t++] = o + b;
        }

        dirty = false;
        if (mesh == null) { mesh = new Mesh(); mesh.name = "SaturnRing"; }
        mesh.Clear();
        mesh.vertices = v;
        mesh.normals = nor;
        mesh.uv = uv;
        mesh.triangles = tri;
        GetComponent<MeshFilter>().sharedMesh = mesh;
    }
}
