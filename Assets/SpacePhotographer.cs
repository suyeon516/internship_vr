using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using System.Collections.Generic;
using System;
using System.IO;

[System.Serializable]
public class SpaceBody
{
    public string bodyName;                  // 예: SATURN (영어 권장)
    public string category;                  // 예: GAS GIANT
    [TextArea] public string info;           // 도감 화면에 쓸 설명
    public Transform target;                 // 씬에 놓은 천체
    [HideInInspector] public bool captured;
    [HideInInspector] public BodyRecord record;
}

// ===== 도감 저장용 데이터 (JsonUtility로 collection.json 저장) =====
[System.Serializable]
public class ShotRecord
{
    public string photo;                     // photos/ 안의 파일 이름
    public string time;
    public int score;                        // 0~100 종합 점수
    public string grade;                     // S A B C D
    public int aim, framing, distance, stability;   // 항목별 0~100
    public float distanceM;                  // 천체 중심까지 거리
    public float angleDeg;                   // 조준 오차 (도)
    public float fill;                       // 사진 세로 대비 천체 지름 비율
    public float fov;                        // 촬영 화각
}

[System.Serializable]
public class BodyRecord
{
    public string name, category, info, color;
    public bool captured;
    public int bestScore;
    public string bestGrade = "";
    public string bestPhoto = "";
    public List<ShotRecord> shots = new List<ShotRecord>();
}

[System.Serializable]
public class CollectionData
{
    public string app = "SPACE PHOTOGRAPHER";
    public string updated;
    public int total, captured;
    public List<BodyRecord> bodies = new List<BodyRecord>();
}

public class SpacePhotographer : MonoBehaviour
{
    public List<SpaceBody> bodies = new List<SpaceBody>();
    public Camera cam;                       // 비워 두면 활성화된 메인 카메라를 자동으로 씀
    public Transform aimSource;              // 비워 두면 카메라 정면 (나중에 컨트롤러로 교체 가능)
    public float maxDist = 300f;
    public float angleDeg = 8f;
    public float lookSpeed = 0.15f;

    [Header("사진 / 품질 평가")]
    public int photoSize = 1024;             // 저장 사진 해상도 (정사각형)
    public float vrPhotoFov = 40f;           // VR에서 사진 카메라 화각 (PC는 현재 줌 화각을 그대로 씀)
    public float minFov = 10f, maxFov = 60f; // 줌 범위 (화각이 작을수록 확대)
    public float vrZoomSpeed = 40f;          // 조이스틱 끝까지 밀었을 때 초당 화각 변화량
    public float handZoomPerMeter = 150f;    // 왼손 핀치 후 앞뒤로 1m 움직일 때 화각 변화량 (20cm = 30도)
    public Material viewfinderMaterial;      // 비워 두면 Unlit 셰이더로 자동 생성
    public float idealFill = 0.45f;          // 천체가 사진 세로의 45%를 채울 때 구도 만점
    public float shakeLimit = 30f;           // 초당 이 각도 이상 흔들리면 안정성 0점
    public bool loadSaved = true;            // 시작할 때 저장된 도감 불러오기

    const int HudLayer = 5;                  // UI 레이어: 사진 카메라에는 안 찍히게 제외

    const string HandPermission = "android.permission.HAND_TRACKING";

    InputAction shootAction;
    InputAction zoomAction;
    InputAction leftPinchAction;             // 손 추적: 왼손 핀치 잡고 있는지
    InputAction leftHandPos;                 // 손 추적: 왼손 위치 (트래킹 공간 기준)
    bool handZooming;
    float handZoomStartDepth, handZoomStartFov;
    float hintTimer = 10f;
    TextMesh hud;
    TextMesh crosshair;
    GameObject viewfinder;
    float baseFov;
    Camera photoCam;
    RenderTexture photoRT;
    float yaw, pitch;
    string message = "";
    float messageTimer = 0f;
    Quaternion prevAimRot;
    float aimSpeed;                          // 조준 회전 속도 (도/초, 부드럽게 평균)

    bool InVR { get { return XRSettings.isDeviceActive; } }
    Transform Aim { get { return aimSource != null ? aimSource : cam.transform; } }

    // 에디터: 프로젝트/SpaceDex/data (웹 도감이 바로 읽음)
    // 기기:   persistentDataPath/SpaceDex (adb pull로 PC에 가져옴)
    static string DataDir
    {
        get
        {
#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SpaceDex", "data"));
#else
            return Path.Combine(Application.persistentDataPath, "SpaceDex");
#endif
        }
    }

    void OnEnable()
    {
        shootAction = new InputAction("Shoot", InputActionType.Button);
        shootAction.AddBinding("<Mouse>/leftButton");
        shootAction.AddBinding("<XRController>{RightHand}/{TriggerButton}");
        shootAction.AddBinding("<XRController>{RightHand}/triggerPressed");
        shootAction.AddBinding("<XRController>{LeftHand}/{TriggerButton}");
        shootAction.AddBinding("<XRController>{LeftHand}/triggerPressed");
        // 손 추적 (OpenXR Hand Interaction Profile): 오른손 엄지+검지 핀치 = 셔터
        shootAction.AddBinding("<HandInteraction>{RightHand}/pinchTouched");
        shootAction.AddBinding("<HandInteraction>{RightHand}/pointerActivated");
        shootAction.Enable();

        // 손 추적 줌: 왼손 핀치한 채로 앞으로 밀면 확대, 당기면 축소
        leftPinchAction = new InputAction("LeftPinch", InputActionType.Button);
        leftPinchAction.AddBinding("<HandInteraction>{LeftHand}/pinchTouched");
        leftPinchAction.AddBinding("<HandInteraction>{LeftHand}/pointerActivated");
        leftPinchAction.Enable();

        leftHandPos = new InputAction("LeftHandPos", InputActionType.Value, expectedControlType: "Vector3");
        leftHandPos.AddBinding("<HandInteraction>{LeftHand}/pointerPosition");
        leftHandPos.Enable();

        // VR 줌: 조이스틱 앞(+y) = 확대, 뒤(-y) = 축소. 양손 다 됨
        zoomAction = new InputAction("Zoom", InputActionType.Value, expectedControlType: "Vector2");
        zoomAction.AddBinding("<XRController>{RightHand}/{Primary2DAxis}");
        zoomAction.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}");
        zoomAction.Enable();
    }

    void OnDisable()
    {
        if (shootAction != null) shootAction.Disable();
        if (zoomAction != null) zoomAction.Disable();
        if (leftPinchAction != null) leftPinchAction.Disable();
        if (leftHandPos != null) leftHandPos.Disable();
    }

    void Start()
    {
        if (cam == null) cam = Camera.main;
        if (cam == null) cam = FindFirstObjectByType<Camera>();

#if UNITY_ANDROID && !UNITY_EDITOR
        // Android XR은 손 추적 권한을 실행 중에 물어봐야 함 (처음 한 번 팝업)
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(HandPermission))
            UnityEngine.Android.Permission.RequestUserPermission(HandPermission);
#endif

        yaw = cam.transform.eulerAngles.y;
        pitch = cam.transform.eulerAngles.x;
        prevAimRot = Aim.rotation;
        baseFov = cam.fieldOfView;
        MakeHud();
        MakePhotoCamera();
        MakeViewfinder();

        foreach (SpaceBody b in bodies) b.record = NewRecord(b);
        if (loadSaved) LoadCollection();
        SaveCollection();                    // 천체 목록/설명이 바뀌었으면 바로 반영
        Debug.Log("[SpacePhotographer] 도감 저장 위치: " + DataDir);
    }

    void MakeHud()
    {
        // 정보 글자: 카메라 앞 1.5m, 약간 아래
        GameObject g = new GameObject("HUD");
        g.transform.SetParent(cam.transform, false);
        g.transform.localPosition = new Vector3(0f, -0.4f, 1.5f);
        hud = g.AddComponent<TextMesh>();
        hud.fontSize = 100;
        hud.characterSize = 0.008f;          // 글자 크기: 안 맞으면 이 값 조절
        hud.anchor = TextAnchor.UpperCenter;
        hud.alignment = TextAlignment.Center;
        hud.color = Color.white;
        g.layer = HudLayer;

        // 조준점
        GameObject c = new GameObject("Crosshair");
        c.transform.SetParent(cam.transform, false);
        c.transform.localPosition = new Vector3(0f, 0f, 2f);
        crosshair = c.AddComponent<TextMesh>();
        crosshair.text = "+";
        crosshair.fontSize = 100;
        crosshair.characterSize = 0.01f;
        crosshair.anchor = TextAnchor.MiddleCenter;
        crosshair.alignment = TextAlignment.Center;
        crosshair.color = Color.white;
        c.layer = HudLayer;
    }

    // 사진 전용 카메라: XR 카메라를 직접 렌더하면 눈 하나짜리/검은 화면이 나올 수 있어서 따로 둠
    void MakePhotoCamera()
    {
        GameObject g = new GameObject("PhotoCamera");
        g.transform.SetParent(Aim, false);
        photoCam = g.AddComponent<Camera>();
        photoCam.CopyFrom(cam);
        photoCam.transform.localPosition = Vector3.zero;
        photoCam.transform.localRotation = Quaternion.identity;
        photoCam.stereoTargetEye = StereoTargetEyeMask.None;
        photoCam.aspect = 1f;
        photoCam.cullingMask &= ~(1 << HudLayer);
        photoCam.enabled = false;            // PC: Render()를 부를 때만 찍힘 / VR: 뷰파인더용으로 매 프레임

        photoRT = new RenderTexture(photoSize, photoSize, 24);
        photoCam.targetTexture = photoRT;
    }

    // VR 뷰파인더: 머리 화각은 못 바꾸니까 사진 카메라 화면을 오른쪽 아래 작은 화면에 띄움
    void MakeViewfinder()
    {
        viewfinder = GameObject.CreatePrimitive(PrimitiveType.Quad);
        viewfinder.name = "Viewfinder";
        Destroy(viewfinder.GetComponent<Collider>());
        viewfinder.layer = HudLayer;
        viewfinder.transform.SetParent(cam.transform, false);
        viewfinder.transform.localPosition = new Vector3(0.28f, -0.18f, 1f);
        viewfinder.transform.localScale = Vector3.one * 0.3f;

        Material m = viewfinderMaterial;
        if (m == null)
        {
            Shader s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s == null) s = Shader.Find("Unlit/Texture");
            m = new Material(s);
        }
        Renderer vr = viewfinder.GetComponent<Renderer>();
        vr.material = m;
        vr.material.mainTexture = photoRT;   // .material은 복사본이라 원본 머티리얼은 안 바뀜

        // 뷰파인더 가운데 조준점
        GameObject c = new GameObject("ViewfinderCross");
        c.layer = HudLayer;
        c.transform.SetParent(viewfinder.transform, false);
        c.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        TextMesh t = c.AddComponent<TextMesh>();
        t.text = "+";
        t.fontSize = 100;
        t.characterSize = 0.02f;
        t.anchor = TextAnchor.MiddleCenter;
        t.color = Color.white;

        viewfinder.SetActive(false);
    }

    float PhotoFov { get { return InVR ? vrPhotoFov : cam.fieldOfView; } }

    // 60도 기준 배율 (화각이 반이면 약 2배)
    float ZoomX { get { return Mathf.Tan(30f * Mathf.Deg2Rad) / Mathf.Tan(PhotoFov * 0.5f * Mathf.Deg2Rad); } }

    void Update()
    {
        // PC일 때만: 우클릭 드래그로 시점 회전, 휠로 줌
        Mouse mouse = Mouse.current;
        if (!InVR && mouse != null)
        {
            if (mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                float s = lookSpeed * (cam.fieldOfView / 60f);
                yaw += d.x * s;
                pitch = Mathf.Clamp(pitch - d.y * s, -89f, 89f);
                cam.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0)
                cam.fieldOfView = Mathf.Clamp(cam.fieldOfView - Mathf.Sign(scroll) * 4f, minFov, maxFov);
        }

        // VR: 조이스틱 앞뒤로 사진 카메라 줌 (데드존 0.2)
        if (InVR)
        {
            float y = zoomAction.ReadValue<Vector2>().y;
            if (Mathf.Abs(y) > 0.2f)
                vrPhotoFov = Mathf.Clamp(vrPhotoFov - y * vrZoomSpeed * Time.deltaTime, minFov, maxFov);
            HandZoom();
            photoCam.fieldOfView = vrPhotoFov;
        }
        photoCam.enabled = InVR;             // VR일 때만 뷰파인더 실시간 렌더
        viewfinder.SetActive(InVR);

        KeepHudSize();

        // 손떨림 측정: 프레임마다 조준 방향이 몇 도 돌았는지
        if (Time.deltaTime > 0)
        {
            float now = Quaternion.Angle(prevAimRot, Aim.rotation) / Time.deltaTime;
            aimSpeed = Mathf.Lerp(aimSpeed, now, 0.2f);
            prevAimRot = Aim.rotation;
        }

        // 촬영 (마우스 좌클릭 또는 컨트롤러 트리거)
        if (shootAction.WasPressedThisFrame()) Shoot();

        UpdateHud();
    }

    void Shoot()
    {
        Transform aim = Aim;

        SpaceBody best = null;
        float bestDot = Mathf.Cos(angleDeg * Mathf.Deg2Rad);

        foreach (SpaceBody b in bodies)
        {
            if (b.target == null) continue;
            Vector3 to = b.target.position - aim.position;
            if (to.magnitude > maxDist) continue;

            float dot = Vector3.Dot(aim.forward, to.normalized);   // 내적으로 조준 판정
            if (dot > bestDot) { bestDot = dot; best = b; }
        }

        if (best == null) { Say("NOTHING TO CAPTURE"); return; }

        ShotRecord shot = Evaluate(best, aim);
        shot.photo = TakePhoto(best);
        shot.time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        BodyRecord r = best.record;
        r.shots.Add(shot);
        bool first = !best.captured;
        bool newBest = shot.score > r.bestScore;
        best.captured = r.captured = true;
        if (newBest)
        {
            r.bestScore = shot.score;
            r.bestGrade = shot.grade;
            r.bestPhoto = shot.photo;
        }
        SaveCollection();

        string head = first ? "CAPTURED! " + best.bodyName
                    : newBest ? "NEW BEST! " + best.bodyName
                    : best.bodyName + "  (BEST " + r.bestScore + ")";
        Say(head + "\n" + shot.score + " PTS  [" + shot.grade + "]\n" +
            "AIM " + shot.aim + "  FRAME " + shot.framing +
            "  DIST " + shot.distance + "  STEADY " + shot.stability);
    }

    // ===== 사진 품질 평가 =====
    // 조준 40% + 구도(화면 차지율) 30% + 거리 15% + 안정성 15%
    ShotRecord Evaluate(SpaceBody b, Transform aim)
    {
        Vector3 to = b.target.position - aim.position;
        float dist = to.magnitude;
        float angle = Vector3.Angle(aim.forward, to);

        // 1) 조준: 정중앙 0도 = 100, 판정 한계(angleDeg) = 0
        float aimS = 1f - Mathf.Clamp01(angle / angleDeg);

        // 2) 구도: 천체가 사진에서 보이는 크기(시직경) / 사진 화각
        float radius = BodyRadius(b.target);
        float angDiam = 2f * Mathf.Atan2(radius, Mathf.Max(dist, 0.01f));
        float fill = angDiam / (PhotoFov * Mathf.Deg2Rad);
        float k = (fill - idealFill) / 0.25f;
        float frameS = Mathf.Exp(-k * k);    // idealFill에서 최고, 너무 작거나 꽉 차면 감점

        // 3) 거리: 가까울수록 디테일이 좋다고 보고 선형 감점
        float distS = 1f - Mathf.Clamp01((dist - radius) / maxDist);

        // 4) 안정성: 셔터 누를 때 조준이 얼마나 빨리 움직였나
        float steadyS = 1f - Mathf.Clamp01(aimSpeed / shakeLimit);

        float total = 0.40f * aimS + 0.30f * frameS + 0.15f * distS + 0.15f * steadyS;

        ShotRecord s = new ShotRecord();
        s.aim = Mathf.RoundToInt(aimS * 100);
        s.framing = Mathf.RoundToInt(frameS * 100);
        s.distance = Mathf.RoundToInt(distS * 100);
        s.stability = Mathf.RoundToInt(steadyS * 100);
        s.score = Mathf.RoundToInt(total * 100);
        s.grade = Grade(s.score);
        s.distanceM = Round2(dist);
        s.angleDeg = Round2(angle);
        s.fill = Round2(fill);
        s.fov = Round2(PhotoFov);
        return s;
    }

    static string Grade(int score)
    {
        if (score >= 90) return "S";
        if (score >= 75) return "A";
        if (score >= 60) return "B";
        if (score >= 40) return "C";
        return "D";
    }

    static float Round2(float v) { return Mathf.Round(v * 100f) / 100f; }

    static float BodyRadius(Transform t)
    {
        Renderer r = t.GetComponentInChildren<Renderer>();
        if (r == null) return t.lossyScale.x * 0.5f;
        Vector3 e = r.bounds.extents;
        return Mathf.Max(e.x, Mathf.Max(e.y, e.z));
    }

    // ===== 사진 저장 (RenderTexture -> PNG) =====
    string TakePhoto(SpaceBody body)
    {
        string folder = Path.Combine(DataDir, "photos");
        Directory.CreateDirectory(folder);
        string fileName = SafeName(body.bodyName) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";

        photoCam.fieldOfView = PhotoFov;
        photoCam.Render();                   // HUD는 UI 레이어라 사진에 안 찍힘

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = photoRT;
        Texture2D tex = new Texture2D(photoSize, photoSize, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, photoSize, photoSize), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;

        File.WriteAllBytes(Path.Combine(folder, fileName), tex.EncodeToPNG());
        Destroy(tex);
        return fileName;
    }

    static string SafeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "BODY";
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }

    // ===== 도감 JSON 저장/불러오기 =====
    BodyRecord NewRecord(SpaceBody b)
    {
        BodyRecord r = new BodyRecord();
        r.name = b.bodyName;
        r.category = b.category;
        r.info = b.info;
        r.color = "888888";
        if (b.target != null)
        {
            Renderer rd = b.target.GetComponentInChildren<Renderer>();
            if (rd != null && rd.sharedMaterial != null && rd.sharedMaterial.HasProperty("_BaseColor"))
                r.color = ColorUtility.ToHtmlStringRGB(rd.sharedMaterial.GetColor("_BaseColor"));
        }
        return r;
    }

    void LoadCollection()
    {
        string path = Path.Combine(DataDir, "collection.json");
        if (!File.Exists(path)) return;

        CollectionData data = JsonUtility.FromJson<CollectionData>(File.ReadAllText(path));
        if (data == null || data.bodies == null) return;

        foreach (SpaceBody b in bodies)
        {
            BodyRecord saved = data.bodies.Find(x => x.name == b.bodyName);
            if (saved == null || saved.shots == null) continue;

            // photos 폴더에서 지운 사진은 기록에서도 뺌 → 남은 사진으로 최고점/대표 사진 다시 계산
            string photoDir = Path.Combine(DataDir, "photos");
            List<ShotRecord> kept = saved.shots.FindAll(s =>
                !string.IsNullOrEmpty(s.photo) && File.Exists(Path.Combine(photoDir, s.photo)));

            b.record.shots = kept;
            b.record.captured = b.captured = kept.Count > 0;
            b.record.bestScore = 0; b.record.bestGrade = ""; b.record.bestPhoto = "";
            foreach (ShotRecord s in kept)
                if (s.score > b.record.bestScore || b.record.bestPhoto == "")
                {
                    b.record.bestScore = s.score;
                    b.record.bestGrade = s.grade;
                    b.record.bestPhoto = s.photo;
                }
        }
    }

    void SaveCollection()
    {
        CollectionData data = new CollectionData();
        data.updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        data.total = bodies.Count;
        foreach (SpaceBody b in bodies)
        {
            data.bodies.Add(b.record);
            if (b.captured) data.captured++;
        }

        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "collection.json"), JsonUtility.ToJson(data, true));
    }

    [ContextMenu("Reset Collection")]
    void ResetCollection()
    {
        string path = Path.Combine(DataDir, "collection.json");
        if (File.Exists(path)) File.Delete(path);
        foreach (SpaceBody b in bodies) { b.captured = false; b.record = NewRecord(b); }
        Debug.Log("[SpacePhotographer] 도감 초기화 (사진 파일은 photos 폴더에 남아 있음)");
    }

    // 왼손 핀치 시작 위치 기준으로, 머리 정면 방향으로 얼마나 밀었/당겼는지로 화각 조절
    void HandZoom()
    {
        if (!leftPinchAction.IsPressed()) { handZooming = false; return; }

        // 손 위치는 트래킹 공간 기준 → XR Origin의 Camera Offset(카메라 부모) 기준으로 월드 변환
        Vector3 p = leftHandPos.ReadValue<Vector3>();
        Transform space = cam.transform.parent;
        Vector3 world = space != null ? space.TransformPoint(p) : p;
        float depth = Vector3.Dot(world - cam.transform.position, cam.transform.forward);

        if (!handZooming)
        {
            handZooming = true;
            handZoomStartDepth = depth;
            handZoomStartFov = vrPhotoFov;
        }
        vrPhotoFov = Mathf.Clamp(handZoomStartFov - (depth - handZoomStartDepth) * handZoomPerMeter, minFov, maxFov);
    }

    // 줌하면 카메라 앞 글자도 같이 확대돼 보이니까, 화각에 맞춰 크기/위치를 반대로 줄여서 화면상 크기 고정
    void KeepHudSize()
    {
        float k = InVR ? 1f : Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(baseFov * 0.5f * Mathf.Deg2Rad);
        hud.transform.localScale = Vector3.one * k;
        hud.transform.localPosition = new Vector3(0f, -0.4f * k, 1.5f);
        crosshair.transform.localScale = Vector3.one * k;
    }

    void Say(string s) { message = s; messageTimer = 4f; }

    void UpdateHud()
    {
        int count = 0;
        foreach (SpaceBody b in bodies) if (b.captured) count++;

        string text = "MY SPACE COLLECTION  " + count + " / " + bodies.Count + "   ZOOM x" + ZoomX.ToString("0.0");
        if (SolarSystem.Instance != null) text += "\n" + SolarSystem.Instance.StatusLine;
        if (messageTimer > 0)
        {
            text += "\n" + message;
            messageTimer -= Time.deltaTime;
        }
        else if (InVR && hintTimer > 0)
        {
            // 시작 10초 동안 조작법 안내
            text += "\nRIGHT PINCH = SHOOT\nLEFT PINCH + PUSH / PULL = ZOOM";
            hintTimer -= Time.deltaTime;
        }
        hud.text = text;
    }

    void OnDestroy()
    {
        if (photoRT != null) photoRT.Release();
    }
}
