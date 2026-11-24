'use strict';
const OrbitNineteen = (() => {
  const earthRadius = 6378136.3;
  const mu = 3.986004415e14;
  const j = 0.0016239391125;
  const tau = 2 * Math.PI;
  const radians = Math.PI / 180;
  const epoch2016 = Date.UTC(2016, 0, 1);
  const dayMs = 86400000;

  // Polynomial terms are in arcseconds, with constants in degrees.
  const fundamentalAngles = [
    [134.96340251, 1717915923.2178, 31.8792, 0.051635, -0.0002447],
    [357.52910918, 129596581.0418, -0.5532, 0.000136, -0.00001149],
    [93.27209062, 1739527262.8478, -12.7512, -0.001037, 0.00000417],
    [297.85019547, 1602961601.209, -6.3706, 0.006593, -0.00003169],
    [125.04455501, -6962890.5431, -7.4722, -0.007702, 0.00005939]
  ];
  const nutationTerms = [
    // Multipliers of l, l′, F, D, Ω | longitude: sin, T sin, cos, T cos | obliquity: cos, T cos, sin, T sin.
    [[0,0,0,0,1], [-172064.161,-174.666,33.386,0.029], [92052.2331,9.086,15.377,0.002]],
    [[0,0,2,-2,2], [-13170.906,-1.675,-13.696,0.012], [5370.336,-3.015,-4.587,-0.003]],
    [[0,0,2,0,2], [-2276.413,-0.234,2.796,0.002], [978.459,-0.485,1.374,-0.001]],
    [[0,0,0,0,2], [2074.554,0.207,-0.698,0], [-897.492,0.47,-0.291,0]],
    [[0,1,0,0,0], [1475.877,-3.633,11.817,-0.015], [73.871,-0.184,-1.924,0.005]]
  ];

  // Geopotential layer base in m, temperature K, lapse K/m, pressure Pa.
  const atmosphereLayers = [
    [0,288.15,-0.0065,101325],
    [11000,216.65,0,22632.1],
    [20000,216.65,0.001,5474.88],
    [32000,228.65,0.0028,868.018],
    [47000,270.6,0,110.906],
    [51000,270.65,-0.0028,66.9387],
    [71000,214.65,-0.002,3.95641]
  ];

  // Spherical-density model. Height breakpoint is in km; remaining coefficients form log10(density).
  const densityLayers = [
    [85,-5.08515,-0.0733375,-0.000825405,4.79193e-05],
    [90,-5.46648,-0.0779976,-0.000106615,5.61803e-06],
    [100,-6.2515,-0.0784445,6.19256e-05,1.68843e-05],
    [110,-7.01287,-0.0721407,0.000568454,2.41767e-05],
    [120,-7.65326,-0.0535188,0.00129374,-2.96605e-05],
    [130,-8.08874,-0.0365437,0.000403772,-2.89208e-06],
    [140,-8.41669,-0.0293359,0.00031701,-4.42685e-06],
    [150,-8.68277,-0.0243238,0.000184205,-1.44722e-06],
    [160,-8.90904,-0.0210738,0.000140788,-1.37461e-06],
    [170,-9.10707,-0.0186705,9.95495e-05,-6.77454e-07],
    [180,-9.2845,-0.0168827,7.92259e-05,-5.93218e-07],
    [190,-9.446,-0.0154761,6.14293e-05,-3.81119e-07],
    [200,-9.595,-0.0143619,4.99958e-05,-2.49568e-07],
    [220,-9.86423,-0.0126615,3.50217e-05,-1.54281e-07],
    [240,-10.1047,-0.0114458,2.57648e-05,-9.25137e-08],
    [260,-10.324,-0.0105262,2.0214e-05,-7.74691e-08],
    [280,-10.5271,-0.00981064,1.55659e-05,-6.50883e-08],
    [300,-10.7176,-0.00926611,1.16606e-05,-2.49222e-08],
    [400,-11.5525,-0.00768166,4.18393e-06,-3.88723e-09],
    [500,-12.2827,-0.00696149,3.01776e-06,4.47753e-09],
    [600,-12.9442,-0.00622361,4.36102e-06,9.98741e-09],
    [700,-13.513,-0.00505179,7.35724e-06,-1.24098e-11],
    [800,-13.9446,-0.00358071,7.35352e-06,-1.04955e-08],
    [900,-14.2397,-0.00242487,4.20487e-06,-8.33682e-09]
  ];

  const wrap = angle => angle - tau * Math.floor(angle / tau);

  function meanElements(initial) {
    const { a, e, i, node, periapsis, anomaly } = initial;
    const eccentric = wrap(Math.atan2(Math.sqrt(1 - e * e) * Math.sin(anomaly), e + Math.cos(anomaly)));
    const meanAnomaly = eccentric - e * Math.sin(eccentric);
    let current = { a, e, i, node, periapsis, meanAnomaly };
    for (let step = 0; step < 3; step++) {
      const b = current.a / (step === 0 ? 6378140 : earthRadius);
      const u = current.periapsis + current.meanAnomaly;
      const sinI2 = Math.sin(current.i) ** 2;
      const g = j / (b * (1 - current.e ** 2)) ** 2;
      const x = e * Math.cos(periapsis) - g * ((1 - 1.25 * sinI2) * Math.cos(u) + 7 / 12 * sinI2 * Math.cos(3 * u));
      const y = -e * Math.sin(periapsis) - g * ((1.75 * sinI2 - 1) * Math.sin(u) - 7 / 12 * sinI2 * Math.sin(3 * u));
      const nextPeriapsis = wrap(Math.atan2(-y, x));
      const next = {
        a: a - earthRadius * j / b * sinI2 * Math.cos(2 * u),
        e: Math.hypot(x, y),
        i: wrap(i - 0.25 * g * Math.sin(2 * current.i) * Math.cos(2 * u)),
        node: wrap(node - 0.5 * g * Math.cos(current.i) * Math.sin(2 * u)),
        periapsis: nextPeriapsis,
        meanAnomaly: wrap(meanAnomaly + periapsis - nextPeriapsis - g * (1.25 * sinI2 - 0.5) * Math.sin(2 * u))
      };
      const settled = next.e > 0 &&
        [next.a / earthRadius - b, next.i - current.i, next.node - current.node,
          next.periapsis + next.meanAnomaly - u].every(delta => Math.abs(delta) <= 1e-5);
      current = next;
      if (settled) break;
    }
    return current;
  }

  function lowerLayer(layers, height) {
    let index = layers.length - 1;
    while (index > 0 && height < layers[index][0]) index--;
    return layers[index];
  }

  function density(height) {
    if (height >= 1000000) return 0;
    if (height >= 86000) {
      const h = height / 1000;
      const [base, c0, c1, c2, c3] = lowerLayer(densityLayers, h);
      const z = h - base;
      return 10 ** (c0 + z * (c1 + z * (c2 + z * c3)));
    }
    const geopotential = 6356766 * height / (6356766 + height);
    const [base, temperature, lapse, pressure] = lowerLayer(atmosphereLayers, geopotential);
    const dz = geopotential - base;
    const localTemperature = temperature + lapse * dz;
    const gasConstant = 287.053, gravity = 9.80665;
    const localPressure = lapse === 0 ?
      pressure * Math.exp(-gravity * dz / (gasConstant * temperature)) :
      pressure * (localTemperature / temperature) ** (-gravity / (gasConstant * lapse));
    return localPressure / (gasConstant * localTemperature);
  }

  function nutation(t) {
    const angles = fundamentalAngles.map(([base, a, b, c, d]) => base + t * (a + t * (b + t * (c + t * d))) / 3600);
    let longitude = 0, obliquity = 0;
    for (const [factors, psi, eps] of nutationTerms) {
      const angle = factors.reduce((sum, factor, i) => sum + factor * angles[i], 0) * radians;
      const sin = Math.sin(angle), cos = Math.cos(angle);
      longitude += (psi[0] + t * psi[1]) * sin + (psi[2] + t * psi[3]) * cos;
      obliquity += (eps[0] + t * eps[1]) * cos + (eps[2] + t * eps[3]) * sin;
    }
    return {
      longitude: longitude / 36000000,
      obliquity: 23.439291111111114 - 0.013011177777777778 * t - 1.638888888888889e-7 * t ** 2 +
        5.03611111111111e-7 * t ** 3 + obliquity / 36000000
    };
  }

  function compute(initial, epochMs, inputOffset = 0, initX = 0) {
    if (!Object.values(initial).every(Number.isFinite) || ![epochMs,inputOffset,initX].every(Number.isFinite))
      throw new RangeError('请填写有效的六根数、历元和初始化量。');
    if (initial.e < 0 || initial.e >= 1) throw new RangeError('偏心率应在 0 到 1 之间，且小于 1。');
    if (initial.i < 0 || initial.i > Math.PI) throw new RangeError('轨道倾角应在 0 到 π 弧度之间。');
    if (initial.a * (1 - initial.e) <= earthRadius)
      throw new RangeError('近地点半径必须大于地球参考半径，请核对半长轴和偏心率。');
    if (initX >= 12782.9) throw new RangeError('初始化量 x 必须小于 12782.9。');

    const { a, e, i, node, periapsis, meanAnomaly } = meanElements(initial);
    if (!(a > 0 && e >= 0 && e < 1)) throw new RangeError('该组根数无法得到有效的平均椭圆轨道。');
    const n = Math.sqrt(mu / a ** 3), p = a * (1 - e * e) / earthRadius;
    const s2 = Math.sin(i) ** 2, c = Math.cos(i), k = j * n / (p * p);
    const x = e * Math.cos(periapsis), y = -e * Math.sin(periapsis);
    const w1 = k * (2 - 2.5 * s2);
    const f = meanAnomaly + 2 * e * Math.sin(meanAnomaly) + 1.25 * e * e * Math.sin(2 * meanAnomaly);
    const ratio = (1 + e * e + 2 * e * Math.cos(f)) / (1 - e * e);
    const radius = initial.a * (1 - initial.e ** 2) / (1 + initial.e * Math.cos(initial.anomaly));
    const speed = Math.sqrt(mu * (2 / radius - 1 / initial.a));
    const rotation = 1 - 7.2921154e-5 * (radius / speed) * c;
    const b = 2 * 9.079202768874502 / (12782.9 - initX) * density(radius - earthRadius) * n * a * rotation ** 2;
    const da = -b * a * ratio ** 1.5;
    const de = -b * (Math.cos(f) + e) * Math.sqrt(ratio);

    // Preserve the Julian-day rounding of the source formula before computing the century offset.
    const julianDay = epochMs / dayMs + 2440587.5;
    const t = (julianDay - 2451545.0) / 36525;
    const msun = (357.5291 + t * (35999.05028888889 + t * (-0.00015611111111111113 - 0.000003333333333333333 * t))) % 360;
    const angles = nutation(t);
    const sidereal = (67310.54841 + t * (3164400184.812866 + t * (0.093104 - 0.0000062 * t))) * Math.PI / 43200;
    const result = {
      Da0: a - earthRadius, A: e, alpha: Math.atan2(y, x),
      Msun: msun * radians, lamdaG: (sidereal + angles.longitude * radians * Math.cos(angles.obliquity * radians)) % tau,
      OMG0: node, OMG1: -k * c, lamda0: (periapsis + meanAnomaly) % tau,
      n1: n + k * (Math.sqrt(1 - e * e) * (1 - 1.5 * s2) + 2 - 2.5 * s2),
      i0: i, t0: Math.trunc(1000 * inputOffset + 0.5) / 1000, w1,
      ksi2: (epochMs - epoch2016) / 1000 - inputOffset,
      ac: 66062854260.665085 * c, as: 66062854260.665085 * s2,
      lamda2: 0.75 * (n / a) * da, aD: da, ksiD: de * Math.cos(periapsis) + w1 * y,
      etaD: -de * Math.sin(periapsis) - w1 * x
    };
    if (!(n > 0) || !Object.values(result).every(Number.isFinite))
      throw new RangeError('计算结果超出数值范围，请核对输入。');
    return result;
  }

  return { compute };
})();