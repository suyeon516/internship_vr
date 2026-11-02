using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

[System.Serializable]
public class SpaceBody
{
    public string bodyName;                  // 예: 토성
    public string category;                  // 예: 가스 행성
    [TextArea] public string info;           // 도감에 보일 설명
    public Transform target;                 // 씬에 놓은 천체
    [HideInInspector] public bool captured;  // 촬영했는지
}

public class SpacePhotographer : MonoBehaviour   // Main Camera에 붙임
{
    public List<SpaceBody> bodies = new List<SpaceBody>();
    public float maxDist = 300f;             // 이 거리 안의 천체만 촬영 가능
    public float angleDeg = 8f;              // 화면 중앙에서 이 각도 안이면 촬영 성공
    public float lookSpeed = 0.15f;

    Camera cam;
    float yaw, pitch;
    string message = "";
    float messageTimer = 0f;

    void Start()
    {
        cam = GetComponent<Camera>();
        yaw = transform.eulerAngles.y;
        pitch = transform.eulerAngles.x;
    }

    void Update()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        // 우클릭 드래그: 시점 회전 (줌했을 때는 천천히)
        if (mouse.rightButton.isPressed)
        {
            Vector2 d = mouse.delta.ReadValue();
            float s = lookSpeed * (cam.fieldOfView / 60f);
            yaw += d.x * s;
            pitch = Mathf.Clamp(pitch - d.y * s, -89f, 89f);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        }

        // 휠: 줌 (시야각 10~60도)
        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0)
            cam.fieldOfView = Mathf.Clamp(cam.fieldOfView - Mathf.Sign(scroll) * 4f, 10f, 60f);

        // 좌클릭: 촬영
        if (mouse.leftButton.wasPressedThisFrame) Shoot();

        if (messageTimer > 0) messageTimer -= Time.deltaTime;
    }

    void Shoot()
    {
        SpaceBody best = null;
        float bestDot = Mathf.Cos(angleDeg * Mathf.Deg2Rad);   // 이보다 커야 화면 중앙 근처

        foreach (SpaceBody b in bodies)
        {
            Vector3 to = b.target.position - transform.position;
            if (to.magnitude > maxDist) continue;

            float dot = Vector3.Dot(transform.forward, to.normalized);   // 내적으로 조준 판정
            if (dot > bestDot) { bestDot = dot; best = b; }
        }

        if (best == null) { Say("촬영할 천체가 없어요"); return; }

        if (best.captured) Say(best.bodyName + ": 이미 촬영했어요");
        else
        {
            best.captured = true;
            Say("촬영 성공! " + best.bodyName + " (" + best.category + ")");
        }
    }

    void Say(string s) { message = s; messageTimer = 3f; }

    void OnGUI()
    {
        int count = 0;
        foreach (SpaceBody b in bodies) if (b.captured) count++;

        GUI.Label(new Rect(10, 10, 400, 25), "MY SPACE COLLECTION  " + count + " / " + bodies.Count);
        GUI.Label(new Rect(Screen.width / 2 - 5, Screen.height / 2 - 10, 20, 20), "+");   // 조준점
        if (messageTimer > 0)
            GUI.Label(new Rect(Screen.width / 2 - 150, Screen.height - 80, 300, 25), message);
    }
}