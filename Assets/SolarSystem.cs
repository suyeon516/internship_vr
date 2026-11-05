using UnityEngine;
using UnityEngine.InputSystem;
using System;
using System.Collections.Generic;

// 태양계 N체 시뮬레이션
// - 시작 상태: 실제 날짜의 행성 위치/속도 (JPL 근사 궤도요소 -> 케플러 방정식으로 변환)
// - 그 다음부터는 만유인력 F = G m1 m2 / r^2 만으로 10개 천체를 서로 끌어당기며 적분 (Velocity Verlet)
// - 물리 계산은 전부 SI 단위 double. 화면 표시만 따로 축소 (거리 압축, 크기 과장)
public class SolarSystem : MonoBehaviour
{
    public const double G = 6.67430e-11;         // 만유인력 상수 (m^3 kg^-1 s^-2)
    public const double AU = 1.495978707e11;     // 천문단위 (m)
    const double Deg = Math.PI / 180.0;
    const double Obliquity = 23.4392911 * Deg;   // 황도 경사 (적도좌표 -> 황도좌표 변환용)

    public enum Look { Star, Rocky, Earth, Gas, Ice }

    public class Body
    {
        public string name, nameKo, category, desc;
        public double mass;                      // kg
        public double radius;                    // 평균 반지름 m
        public double rotationHours;             // 항성 자전주기 h (음수 = 역방향 자전)
        public double poleRA, poleDec;           // 자전축 북극 방향 (IAU, 적도좌표 도)
        public Color color;
        public Look look;
        public int parent = -1;                  // 위성이면 모천체 인덱스 (화면 배치용)

        public Vector3d pos, vel, acc;           // 질량중심 기준 황도좌표 (m, m/s, m/s^2)
        public Vector3d pole;                    // 자전축 (황도좌표 단위벡터)
        public Transform view;
    }

    [Header("시간")]
    public string startDate = "";                // 비우면 오늘 날짜(UTC), 예: 2026-11-05
    public int speedIndex = 2;                   // timeScales 중 시작 배속
    public double maxStep = 3600;                // 적분 한 스텝 최대 크기 (s). 달 공전 27일 대비 충분히 작게
    public int maxStepsPerFrame = 2000;

    [Header("화면 표시 (물리 계산과 무관)")]
    public float distanceScale = 40f;            // 1 AU가 화면에서 몇 유닛인지 (압축 전)
    public float distancePower = 0.5f;           // 거리 압축: 화면거리 = scale * (r/AU)^power  (1이면 실제 비율)
    public float earthRadiusView = 1.2f;         // 지구 반지름이 화면에서 몇 유닛인지
    public float radiusPower = 0.5f;             // 크기 압축: 화면반지름 = earth * (R/R지구)^power
    public float sunRadiusView = 8f;             // 태양은 따로 (그대로 키우면 수성 궤도를 덮음)
    public float moonDistanceView = 4f;          // 지구-달 거리 화면 표시

    static readonly double[] timeScales = { 3600, 6 * 3600, 86400, 3 * 86400, 10 * 86400, 30 * 86400, 100 * 86400 };
    static readonly string[] timeLabels = { "1 HOUR/S", "6 HOURS/S", "1 DAY/S", "3 DAYS/S", "10 DAYS/S", "30 DAYS/S", "100 DAYS/S" };

    public List<Body> bodies = new List<Body>();
    public static SolarSystem Instance { get; private set; }

    public double SimTime { get; private set; }  // 시작 후 흐른 시뮬레이션 시간 (s)
    public DateTime StartDate { get; private set; }
    public DateTime SimDate { get { return StartDate.AddSeconds(SimTime); } }
    public bool Paused { get; private set; }
    public double EnergyDrift { get; private set; }   // |E - E0| / |E0|  (적분 정확도 확인용)
    public double MomentumDrift { get; private set; } // 각운동량 상대 오차

    double energy0;
    Vector3d angMom0;
    InputAction slowerAction, fasterAction, pauseAction;

    // ===== 실제 데이터 =====
    // 질량/반지름: NASA Planetary Fact Sheet, 자전축/자전주기: IAU WGCCRE 2015
    // 궤도요소: JPL "Keplerian Elements for Approximate Positions of the Major Planets" (1800-2050)
    //            a(AU), e, I, L, 근일점경도 ϖ, 승교점경도 Ω (도) + 100년당 변화율
    static readonly double[][] elements =
    {
        null,                                                                                   // Sun
        new[]{ 0.38709927, 0.20563593, 7.00497902, 252.25032350,  77.45779628,  48.33076593,
               0.00000037, 0.00001906,-0.00594749,149472.67411175, 0.16047689, -0.12534081 },   // Mercury
        new[]{ 0.72333566, 0.00677672, 3.39467605, 181.97909950, 131.60246718,  76.67984255,
               0.00000390,-0.00004107,-0.00078890, 58517.81538729, 0.00268329, -0.27769418 },   // Venus
        new[]{ 1.00000261, 0.01671123,-0.00001531, 100.46457166, 102.93768193,   0.0,
               0.00000562,-0.00004392,-0.01294668, 35999.37244981, 0.32327364,  0.0 },          // Earth-Moon 질량중심
        null,                                                                                   // Moon (아래에서 따로)
        new[]{ 1.52371034, 0.09339410, 1.84969142,  -4.55343205, -23.94362959,  49.55953891,
               0.00001847, 0.00007882,-0.00813131, 19140.30268499, 0.44441088, -0.29257343 },   // Mars
        new[]{ 5.20288700, 0.04838624, 1.30439695,  34.39644051,  14.72847983, 100.47390909,
              -0.00011607,-0.00013253,-0.00183714,  3034.74612775, 0.21252668,  0.20469106 },   // Jupiter
        new[]{ 9.53667594, 0.05386179, 2.48599187,  49.95424423,  92.59887831, 113.66242448,
              -0.00125060,-0.00050991, 0.00193609,  1222.49362201,-0.41897216, -0.28867794 },   // Saturn
        new[]{19.18916464, 0.04725744, 0.77263783, 313.23810451, 170.95427630,  74.01692503,
              -0.00196176,-0.00004397,-0.00242939,   428.48202785, 0.40805281,  0.04240589 },   // Uranus
        new[]{30.06992276, 0.00859048, 1.77004347, -55.12002969,  44.96476227, 131.78422574,
               0.00026291, 0.00005105, 0.00035372,   218.45945325,-0.32241464, -0.00508664 },   // Neptune
    };

    static List<Body> RealBodies()
    {
        return new List<Body>
        {
            B("Sun",     "태양", "STAR",               1.98847e30, 6.957e8,   609.12,    286.13,  63.87, new Color(1f, .85f, .4f), Look.Star,
              "태양계 질량의 99.86%를 차지하는 항성. 수소 핵융합으로 빛난다."),
            B("Mercury", "수성", "TERRESTRIAL PLANET", 3.3011e23,  2.4397e6,  1407.6,    281.01,  61.42, new Color(.6f, .58f, .55f), Look.Rocky,
              "태양에 가장 가까운 행성. 궤도 이심률이 0.21로 행성 중 가장 크다."),
            B("Venus",   "금성", "TERRESTRIAL PLANET", 4.8675e24,  6.0518e6, -5832.6,    272.76,  67.16, new Color(.9f, .8f, .55f), Look.Rocky,
              "두꺼운 이산화탄소 대기의 온실효과로 가장 뜨거운 행성. 거꾸로(역방향) 자전한다."),
            B("Earth",   "지구", "TERRESTRIAL PLANET", 5.97217e24, 6.371e6,     23.9345,   0.00,  90.00, new Color(.25f, .45f, .9f), Look.Earth,
              "액체 물이 있는 유일한 행성. 자전축이 23.4도 기울어져 계절이 생긴다."),
            B("Moon",    "달",   "MOON",               7.342e22,   1.7374e6,   655.72,   269.99,  66.54, new Color(.75f, .75f, .72f), Look.Rocky,
              "지구의 위성. 자전주기와 공전주기가 같아서(동주기 자전) 항상 같은 면만 지구를 본다.", 3),
            B("Mars",    "화성", "TERRESTRIAL PLANET", 6.4171e23,  3.3895e6,    24.6229, 317.68,  52.89, new Color(.8f, .38f, .2f), Look.Rocky,
              "산화철 때문에 붉게 보이는 행성. 하루 길이와 자전축 기울기가 지구와 비슷하다."),
            B("Jupiter", "목성", "GAS GIANT",          1.89819e27, 6.9911e7,     9.925,  268.06,  64.50, new Color(.85f, .72f, .55f), Look.Gas,
              "태양계에서 가장 큰 행성. 질량이 나머지 행성을 모두 합친 것의 2.5배다."),
            B("Saturn",  "토성", "GAS GIANT",          5.6834e26,  5.8232e7,    10.656,   40.59,  83.54, new Color(.9f, .8f, .6f), Look.Gas,
              "얼음과 암석 고리를 가진 행성. 평균 밀도가 물보다 낮다."),
            B("Uranus",  "천왕성", "ICE GIANT",        8.6810e25,  2.5362e7,   -17.24,   257.31, -15.18, new Color(.6f, .85f, .9f), Look.Ice,
              "자전축이 98도 기울어져 거의 누운 채로 공전하는 얼음 거인."),
            B("Neptune", "해왕성", "ICE GIANT",        1.02413e26, 2.4622e7,    16.11,   299.36,  43.46, new Color(.3f, .45f, .95f), Look.Ice,
              "태양계 가장 바깥 행성. 천왕성 궤도의 섭동을 계산해서 관측 전에 위치가 예측되었다."),
        };
    }

    static Body B(string n, string ko, string cat, double m, double r, double rotH, double ra, double dec, Color c, Look look, string desc, int parent = -1)
    {
        Body b = new Body();
        b.name = n; b.nameKo = ko; b.category = cat; b.mass = m; b.radius = r; b.rotationHours = rotH;
        b.poleRA = ra; b.poleDec = dec; b.color = c; b.look = look; b.desc = desc; b.parent = parent;
        return b;
    }

    // ===== 초기화 =====
    void Awake()
    {
        Instance = this;
        bodies = RealBodies();

        DateTime date;
        StartDate = DateTime.TryParse(startDate, out date) ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : DateTime.UtcNow;
        SetInitialState(StartDate);

        foreach (Body b in bodies) b.pole = PoleToEcliptic(b.poleRA, b.poleDec);

        ComputeAccelerations();
        energy0 = TotalEnergy();
        angMom0 = AngularMomentum();

        MakeViews();
        UpdateViews();
    }

    void OnEnable()
    {
        slowerAction = new InputAction("Slower", InputActionType.Button, "<Keyboard>/leftBracket");
        fasterAction = new InputAction("Faster", InputActionType.Button, "<Keyboard>/rightBracket");
        pauseAction = new InputAction("Pause", InputActionType.Button, "<Keyboard>/space");
        slowerAction.Enable(); fasterAction.Enable(); pauseAction.Enable();
    }

    void OnDisable()
    {
        if (slowerAction != null) { slowerAction.Disable(); fasterAction.Disable(); pauseAction.Disable(); }
    }

    // 날짜 -> 율리우스일 -> J2000 이후 세기 수
    static double JulianDay(DateTime utc)
    {
        return 2440587.5 + (utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays;
    }

    void SetInitialState(DateTime utc)
    {
        double d = JulianDay(utc) - 2451545.0;   // J2000 이후 일수
        double T = d / 36525.0;                  // 세기
        Body sun = bodies[0];
        sun.pos = Vector3d.zero; sun.vel = Vector3d.zero;

        for (int i = 1; i < bodies.Count; i++)
        {
            double[] el = elements[i];
            if (el == null) continue;
            double mass = bodies[i].mass + (i == 3 ? bodies[4].mass : 0);   // 지구는 지구+달 질량중심
            double mu = G * (sun.mass + mass);
            Vector3d p, v;
            Kepler(mu,
                el[0] + el[6] * T, el[1] + el[7] * T, el[2] + el[8] * T,
                el[3] + el[9] * T, el[4] + el[10] * T, el[5] + el[11] * T, out p, out v);
            bodies[i].pos = p; bodies[i].vel = v;
        }

        // 달: 지구 기준 평균 궤도요소 (a = 384,400 km, e = 0.0549, i = 5.145도)
        Body earth = bodies[3], moon = bodies[4];
        {
            double mu = G * (earth.mass + moon.mass);
            double L = 218.316 + 13.176396 * d;     // 평균 황경
            double node = 125.045 - 0.0529538 * d;  // 승교점 (18.6년 주기로 역행)
            double peri = 83.353 + 0.1114035 * d;   // 근지점 (8.85년 주기로 순행)
            Vector3d rel, relV;
            Kepler(mu, 3.844e8 / AU, 0.0549, 5.145, L, peri, node, out rel, out relV);

            // 위에서 구한 건 지구-달 질량중심 -> 지구와 달로 나눔
            Vector3d bary = earth.pos, baryV = earth.vel;
            double total = earth.mass + moon.mass;
            earth.pos = bary - rel * (moon.mass / total);
            earth.vel = baryV - relV * (moon.mass / total);
            moon.pos = bary + rel * (earth.mass / total);
            moon.vel = baryV + relV * (earth.mass / total);
        }

        // 태양 중심 -> 태양계 전체 질량중심 기준으로 (총 운동량 0, 태양도 목성 때문에 흔들림)
        double M = 0; Vector3d cm = Vector3d.zero, cv = Vector3d.zero;
        foreach (Body b in bodies) { M += b.mass; cm += b.pos * b.mass; cv += b.vel * b.mass; }
        cm = cm / M; cv = cv / M;
        foreach (Body b in bodies) { b.pos -= cm; b.vel -= cv; }
    }

    // 궤도요소 -> 위치/속도 (케플러 방정식 M = E - e sinE 를 뉴턴법으로 풂)
    static void Kepler(double mu, double aAU, double e, double incDeg, double Ldeg, double variDeg, double nodeDeg,
                       out Vector3d pos, out Vector3d vel)
    {
        double a = aAU * AU;
        double I = incDeg * Deg, node = nodeDeg * Deg;
        double w = (variDeg - nodeDeg) * Deg;                 // 근일점 인수
        double M = Math.IEEERemainder((Ldeg - variDeg) * Deg, 2 * Math.PI);   // 평균 근점이각

        double E = M + e * Math.Sin(M);
        for (int k = 0; k < 20; k++)
        {
            double dE = (E - e * Math.Sin(E) - M) / (1 - e * Math.Cos(E));
            E -= dE;
            if (Math.Abs(dE) < 1e-14) break;
        }

        double cosE = Math.Cos(E), sinE = Math.Sin(E), q = Math.Sqrt(1 - e * e);
        double n = Math.Sqrt(mu / (a * a * a));               // 평균 운동
        double rdot = 1 - e * cosE;
        double x = a * (cosE - e), y = a * q * sinE;          // 궤도면 좌표
        double vx = -a * n * sinE / rdot, vy = a * n * q * cosE / rdot;

        double cw = Math.Cos(w), sw = Math.Sin(w), cO = Math.Cos(node), sO = Math.Sin(node), cI = Math.Cos(I), sI = Math.Sin(I);
        Vector3d P = new Vector3d(cw * cO - sw * sO * cI, cw * sO + sw * cO * cI, sw * sI);
        Vector3d Q = new Vector3d(-sw * cO - cw * sO * cI, -sw * sO + cw * cO * cI, cw * sI);
        pos = P * x + Q * y;
        vel = P * vx + Q * vy;
    }

    static Vector3d PoleToEcliptic(double raDeg, double decDeg)
    {
        double ra = raDeg * Deg, dec = decDeg * Deg;
        double x = Math.Cos(dec) * Math.Cos(ra), y = Math.Cos(dec) * Math.Sin(ra), z = Math.Sin(dec);
        double ce = Math.Cos(Obliquity), se = Math.Sin(Obliquity);
        return new Vector3d(x, y * ce + z * se, -y * se + z * ce).normalized;
    }

    // ===== 물리 =====
    void Update()
    {
        if (slowerAction.WasPressedThisFrame()) speedIndex = Mathf.Max(0, speedIndex - 1);
        if (fasterAction.WasPressedThisFrame()) speedIndex = Mathf.Min(timeScales.Length - 1, speedIndex + 1);
        if (pauseAction.WasPressedThisFrame()) Paused = !Paused;

        if (!Paused)
        {
            double advance = timeScales[speedIndex] * Time.deltaTime;
            int steps = Math.Min(maxStepsPerFrame, Math.Max(1, (int)Math.Ceiling(advance / maxStep)));
            double dt = advance / steps;
            for (int s = 0; s < steps; s++) Step(dt);
            SimTime += advance;

            EnergyDrift = Math.Abs((TotalEnergy() - energy0) / energy0);
            MomentumDrift = (AngularMomentum() - angMom0).magnitude / angMom0.magnitude;
        }
        UpdateViews();
    }

    // Velocity Verlet: v(t+dt/2) -> x(t+dt) -> a(t+dt) -> v(t+dt)
    // 심플렉틱 적분이라 오일러법과 달리 오래 돌려도 에너지가 새지 않음
    void Step(double dt)
    {
        foreach (Body b in bodies) { b.vel += b.acc * (0.5 * dt); b.pos += b.vel * dt; }
        ComputeAccelerations();
        foreach (Body b in bodies) b.vel += b.acc * (0.5 * dt);
    }

    // 모든 쌍에 대해 a_i += G m_j (r_j - r_i) / |r_j - r_i|^3  (뉴턴 제3법칙으로 반대쪽도 같이)
    void ComputeAccelerations()
    {
        foreach (Body b in bodies) b.acc = Vector3d.zero;
        for (int i = 0; i < bodies.Count; i++)
            for (int j = i + 1; j < bodies.Count; j++)
            {
                Vector3d r = bodies[j].pos - bodies[i].pos;
                double d2 = r.sqrMagnitude;
                Vector3d f = r * (G / (d2 * Math.Sqrt(d2)));
                bodies[i].acc += f * bodies[j].mass;
                bodies[j].acc -= f * bodies[i].mass;
            }
    }

    public double TotalEnergy()
    {
        double E = 0;
        for (int i = 0; i < bodies.Count; i++)
        {
            E += 0.5 * bodies[i].mass * bodies[i].vel.sqrMagnitude;               // 운동에너지
            for (int j = i + 1; j < bodies.Count; j++)
                E -= G * bodies[i].mass * bodies[j].mass / (bodies[j].pos - bodies[i].pos).magnitude;   // 위치에너지
        }
        return E;
    }

    public Vector3d AngularMomentum()
    {
        Vector3d L = Vector3d.zero;
        foreach (Body b in bodies) L += Vector3d.Cross(b.pos, b.vel) * b.mass;
        return L;
    }

    // ===== 도감/촬영에서 쓸 값 =====
    public Body Find(string name)
    {
        return bodies.Find(b => string.Equals(b.name, name, StringComparison.OrdinalIgnoreCase));
    }

    // 공전 중심(태양, 달은 지구) 기준 상대 위치/속도
    public Vector3d RelativePos(Body b) { return b.pos - bodies[b.parent >= 0 ? b.parent : 0].pos; }
    public Vector3d RelativeVel(Body b) { return b.vel - bodies[b.parent >= 0 ? b.parent : 0].vel; }

    // 자전축 기울기 = 자전 방향 벡터와 공전 각운동량 방향 사이 각도 (금성 177도, 천왕성 98도)
    public double ObliquityDeg(Body b)
    {
        if (b == bodies[0]) return 7.25;                       // 태양: 황도 기준
        Vector3d spin = b.rotationHours < 0 ? b.pole * -1 : b.pole;
        Vector3d orbitN = Vector3d.Cross(RelativePos(b), RelativeVel(b)).normalized;
        return Math.Acos(Math.Max(-1, Math.Min(1, Vector3d.Dot(spin, orbitN)))) / Deg;
    }

    public string StatusLine
    {
        get
        {
            return SimDate.ToString("yyyy-MM-dd") + "   " + (Paused ? "PAUSED" : timeLabels[speedIndex]);
        }
    }

    // ===== 화면 표시 =====
    // 황도좌표 (x, y, z=북쪽) -> Unity (x, y=위, z). 위에서 보면 반시계방향 공전이 그대로 유지됨
    static Vector3 ToUnity(Vector3d v) { return new Vector3((float)v.x, (float)v.z, (float)v.y); }

    float ViewRadius(Body b)
    {
        if (b == bodies[0]) return sunRadiusView;
        return earthRadiusView * Mathf.Pow((float)(b.radius / 6.371e6), radiusPower);
    }

    Vector3 ViewLocalPos(Body b)
    {
        if (b.parent >= 0)
        {
            // 위성: 모천체 주변에 실제 방향 그대로, 거리만 고정 비율로
            Vector3d rel = RelativePos(b);
            return ViewLocalPos(bodies[b.parent]) + ToUnity(rel) * (float)(moonDistanceView / 3.844e8);
        }
        if (b == bodies[0]) return Vector3.zero;

        Vector3d helio = b.pos - bodies[0].pos;
        double rAU = helio.magnitude / AU;
        return ToUnity(helio.normalized) * distanceScale * Mathf.Pow((float)rAU, distancePower);
    }

    void UpdateViews()
    {
        float s = transform.lossyScale.x;
        foreach (Body b in bodies)
        {
            if (b.view == null) continue;
            b.view.position = transform.TransformPoint(ViewLocalPos(b));

            // 자전: 실제 자전주기로 돌림. Unity는 왼손 좌표계라 위에서 반시계(순행) = 음의 각도
            Vector3 pole = transform.TransformDirection(ToUnity(b.pole));
            double spinDeg = (SimTime / (b.rotationHours * 3600.0) * 360.0) % 360.0;
            b.view.rotation = Quaternion.AngleAxis(-(float)spinDeg, pole) * Quaternion.FromToRotation(Vector3.up, pole);

            Transform p = b.view.parent;
            float ps = p != null ? p.lossyScale.x : 1f;
            b.view.localScale = Vector3.one * (2f * ViewRadius(b) * s / ps);
        }
    }

    // 씬에 같은 이름의 천체가 있으면 그걸 씀 (텍스처 유지), 없으면 구를 만들고 간단한 텍스처를 생성
    void MakeViews()
    {
        SpacePhotographer photographer = FindFirstObjectByType<SpacePhotographer>();

        foreach (Body b in bodies)
        {
            SpaceBody entry = null;
            if (photographer != null)
                entry = photographer.bodies.Find(x => string.Equals(x.bodyName, b.name, StringComparison.OrdinalIgnoreCase));

            if (entry != null && entry.target != null) b.view = entry.target;
            else
            {
                GameObject g = GameObject.Find(b.name);
                if (g == null)
                {
                    g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    g.name = b.name;
                    g.transform.SetParent(transform, false);
                    ApplyLook(g.GetComponent<Renderer>(), b);
                }
                b.view = g.transform;
            }

            if (photographer == null) continue;
            if (entry == null)
            {
                entry = new SpaceBody();
                entry.bodyName = b.name;
                photographer.bodies.Add(entry);
            }
            entry.target = b.view;
            if (string.IsNullOrEmpty(entry.category)) entry.category = b.category;
            if (string.IsNullOrEmpty(entry.info)) entry.info = b.nameKo + " - " + b.desc;
        }
    }

    void ApplyLook(Renderer r, Body b)
    {
        Material m = new Material(r.sharedMaterial);
        Texture2D tex = MakeTexture(b);
        m.mainTexture = tex;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
        if (b.look == Look.Star)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", b.color * 1.5f);
            m.SetTexture("_EmissionMap", tex);
        }
        r.material = m;
    }

    // 자전이 눈에 보이도록: 위도 줄무늬(가스행성) / 얼룩(암석행성) + 경도 0도에 어두운 선 하나
    static Texture2D MakeTexture(Body b)
    {
        const int W = 256, H = 128;
        Texture2D t = new Texture2D(W, H, TextureFormat.RGB24, true);
        float seed = b.name.Length * 13.7f;
        Color[] px = new Color[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float u = (float)x / W, v = (float)y / H;
                float n = Mathf.PerlinNoise(u * 8 + seed, v * 4 + seed);
                Color c = b.color;
                switch (b.look)
                {
                    case Look.Gas: c *= 0.8f + 0.25f * Mathf.Sin(v * 40f + n * 3f); break;
                    case Look.Ice: c *= 0.92f + 0.1f * Mathf.Sin(v * 20f + n * 2f); break;
                    case Look.Rocky: c *= 0.75f + 0.4f * n; break;
                    case Look.Star: c *= 0.9f + 0.2f * n; break;
                    case Look.Earth:
                        c = n > 0.55f ? new Color(.25f, .55f, .25f) : b.color;
                        if (v < 0.08f || v > 0.92f) c = Color.white;   // 극지방 얼음
                        break;
                }
                if (u < 0.012f) c *= 0.5f;                             // 자전 확인용 기준선
                c.a = 1f;
                px[y * W + x] = c;
            }
        t.SetPixels(px);
        t.Apply();
        return t;
    }
}

// double 정밀도 3D 벡터 (Unity Vector3는 float라서 1e12 m 단위 계산에 정밀도가 부족함)
public struct Vector3d
{
    public double x, y, z;
    public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
    public static readonly Vector3d zero = new Vector3d(0, 0, 0);

    public double sqrMagnitude { get { return x * x + y * y + z * z; } }
    public double magnitude { get { return Math.Sqrt(sqrMagnitude); } }
    public Vector3d normalized { get { double m = magnitude; return m > 0 ? this / m : zero; } }

    public static Vector3d operator +(Vector3d a, Vector3d b) { return new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z); }
    public static Vector3d operator -(Vector3d a, Vector3d b) { return new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z); }
    public static Vector3d operator *(Vector3d a, double k) { return new Vector3d(a.x * k, a.y * k, a.z * k); }
    public static Vector3d operator /(Vector3d a, double k) { return new Vector3d(a.x / k, a.y / k, a.z / k); }
    public static double Dot(Vector3d a, Vector3d b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
    public static Vector3d Cross(Vector3d a, Vector3d b)
    {
        return new Vector3d(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    }
}
