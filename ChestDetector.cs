using System;
using System.Collections.Generic;

namespace WtClickerGUI
{
    /// <summary>
    /// 宝箱检测（2026-10-30 联机多桌宠版）：
    /// 宝箱是固定 UI 资产（米橙气泡+棕色宝箱+星光），且与桌宠像素连通、无法用聚块分离。
    /// 检测流程：橙色签名掩码聚类预筛 → 模板匹配（MAD）确认，阈值 10。
    /// 模板取自实机截图（100x78 物理 px），验证过全帧唯一命中（次优 MAD=12）。
    /// </summary>
    internal static class ChestDetector
    {
        const int TW = 100, TH = 78;         // 模板尺寸
        const double MadThreshold = 14.0;    // 判定阈值（新模板 2026-10-31 取自实机；星光动画有微动，放宽到 14）
        static readonly int[] _tB = new int[TW * TH];
        static readonly bool[] _tMask = new bool[TW * TH];   // 气泡本体掩码（比对时忽略背景）
        static readonly int[] _tG = new int[TW * TH];
        static readonly int[] _tR = new int[TW * TH];

        static bool _loaded;
        static string TplB64()
        {
            return
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwIGNyRbnGj3k2XzlGb0lGb0lGb0lGb0lGb0lGb0lGb0lGb0"
                + "lGf0j2HrfFDMek7Jek/Kek/KflHOQClpFA4iCgkMODk/Wl1jWFxjV1tiVVhgQkFKJiUpLi0yRERNSklTMzM5GhMphFrXlmj3k2j1"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQMJSjF6m2r8lGbzlGb0lGb0lGb0lGb0lGb0lGb0lGb0k2f0"
                + "lGj1hljbe07Jek/Kek/KfVDNbEWvEQscFRUYSktRV1tjVlpiVlpiV1tiUFJbNzg+PTxESUhRSklSQEBJDgwTaEmrl2v5k2j1k2j1"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAYOXUGZmWv6lGbzlGb0lGb0lGb0k2f0k2f0k2j1k2j1k2j1"
                + "kWTvf1HQek/Kek/KfFDLYT2dDwoYIiIlVVZfV1phVlpiVlpiVlpiV1tiTk9ZSEZRSUdRSUhSRkVQFxUcRi5ylmb1k2j1k2j1k2j1"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADAgTcU23l2n3k2f0k2f0k2j1k2j1k2j1k2j1k2j1k2j1lGj2"
                + "il7ie1DLe0/LfE7JWzyWDwsYLiwxVVhhVlpiVlpiVlpiVlpiVlpiVlpiUVJcSUdSSUhSSEdQKykvHxYyiWLjlGn2k2j1k2f0lGb0"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADwoZe1PImGf2k2j1k2j1k2j1k2j1k2j1k2j1k2j1k2j1lGj1"
                + "hFfZe0/Kek/JWz2WDgsVMTI5VllgVlpiVlpiVlpiVlpiVlpiVlpiVlpiVllhUFFaTU1WQUFIFRAffFLKlGj1k2j1k2f0lGb0lGb0"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEgwdhV/al2f2k2j1k2j1k2j1k2j1k2j1k2j1k2j1kmn2kGPv"
                + "f1LPe07IYD2cEg4bNTc+VVheVlpiVlpiVlpiVlpiVlpiVlpiVlpiVlpiVlpiVlliTlBYGhcgUTeEk2bxlGb1lGb0lGb0lGb0lGb0"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFA4hi2Lklmj3k2j1k2j1k2j1k2j1k2j1kmn2kmn3kmn3jmHp"
                + "fVHLaUKqGRInMDA3VVheVlpiVlpiVlpiVlpiVlpiVlpiVVlhVVlhVVlhVlpiVVhgLy43LB5GkGHnlGf1lGb0lGb0lGb0lGb0lGb0"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFg4jkGLqlGn2k2j1k2j1k2j1k2j1kmn2kmn3kmn3kmn3i17k"
                + "cUq5JRo7JyYrU1dfVlpiVlpiVlpiVlpiVlpiVVlhVVlhVVphX298XWl2WF1nQkNLHRYqdU+9k2jzlGb0lGb0lGb0lGb0lGXzlGXz"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFQ4ijWLnlWj2k2j1k2j1k2j1kmn3kmn3kmn3kmn3k2n3h1zc"
                + "NSRWIiAnUVNbVlpiVlpiVlpiVlpiVVlhVVlhVVlhWmNtf63QicLqisPra4ihKzA+SzN6k2Xuk2bzk2bzlGb0lGXzlGXzlGXzlGXz"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEg0fh2Ddlmf1kmj0kmj0kmn3kmn3kmn3kmn3k2n2kmrxUzmG"
                + "GxkiTE1UV1tiVlpiVlpiVlpiVVlhVVlhVVlgWGFrboqgj833kNH9kdL+dqPEP0Vqh1vXk2Xxk2Xyk2bzlGXzlGXzlGXzlGXzlGTz"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAsbfFnJl2b0kmfzkWj1kWn2kWn2kWn2kWn2k2n2dlbAHRgq"
                + "QkRKV1phVlpiVlpiVlpiVVlhVVlhVVlgVlpkYXODg7XYkdL+kdL+kdL+cqLFbmW2lWXvkmTwk2Xyk2Xyk2XylGXzlWTzlWTzlWTz"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADQkVcFG5lWj1kWfykGfzkWj1kWj1kWj1kWn2jWjrKx9EOThB"
                + "V1phVlpiVlpiVlpiVlpiVVlhVVlgVFhgW2dzdJizjsz2kdL+kdL+isfwZHurj23mkWPtkWTvkmTwk2TxlGTylWTzlWTzlWTzlWTz"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQEBBgkLFyMs"
                + "JDVBL0RTO1ZoQmB0RmZ7SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/"
                + "SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/SGl/RWV6QVx0bnW6lYf3kHrxkG7xj2nzkGfzkGj0lGj1elPHIhwvT1Fb"
                + "VlpiVlpiVlpiVlpiVVlhVVlhVFhgVVpjZHqMiMDmkdL+kdL+kdL+b5zBfW7MjmXqj2LrkWPtkmTwlGPxlGTylGTylWTzlWPylWPy"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgIDDBIXHSs3Kj5OPVlvVXyZ"
                + "ZpS1cqbKgbzjisfxjs76kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+jc34hb/ohbfpkrb6kKv3j5z1kI31j33yj3HzkGryk2fwSTJ3KSYu"
                + "UVNbV1thVlpiVVlhVVlhVFhgVFhfXGl3dp25kdL+kdL+kdL+i8nzanOyjGbhjGDnj2HqkWPtk2Lvk2PwlGPxlGPxlGPxlWPylWPy"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgIDDRMYHy45O1ZsWoWmcabOh8Xzjs36"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kcf8kK/4j5n1job0kHbyj2bjNiZY"
                + "IyIoTk9YVlpjVVlhVVlgVFhgVltlaoWbisPrkdL+kdL+kNH9dKLLhmnTjF7kjF/mjmDpkWHskmLuk2LvlGLwlGPxlGPxlGPxlWPy"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACxAVGycyO1ZtYpG2fbflhsTyjs76kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kcP8kKP3kIPziGjf"
                + "NCNVGRgeRUdPVlpiVFhgVFhfWmVxdJm0kdL+kdL+kdH9hb/oc3O/imTgil7ijV/mj2DpkGHrkWXtk2zvk2rwlGLwlGLwlGPxlGPx"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgMEDBIXO1dtZ5jAe7Tjgr/uisr3kNH9kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kcX8jJTt"
                + "e13KPipnEg4YMTE3U1VdVVhfV2Brb5Cpjcz2kND7iMTsY4GqiGLXil3gil3ijV/mjmPokG3skYLxkZn2ko/0k2nvk2LvlGLwlGPx"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEBBAUHFB4lXYisd7DefLjnhMHvjc35kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdD+"
                + "jrz2fmTPWz+UGhMrGxkcQkJJU1VeXm18dZ67gbfbYourclm4hlvbiVzgi1/jjGDkjoDskLD3kc79kdL+ksr9kpv2k2Huk2LvlGLw"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAwPMkpdb6POerPifLfmhcLxjs36kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdD+j8T4gHrWdVG/NCNWDAoRKCkuSk1XVFhiIyMoPS1ijWDaiVzfilzgjHHnj6X0kcf8kdH+kdL9kdL+kdD+kcL8kWHsk2Huk2Lv"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADxYbQF94cqjVe7XlfLfnhcLxjs36kdH+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdH+j8b6g5HgelzIWzqTHBQtDQsPIyMlEw4ffFPFh1rdilzgjHzpj7T3kMz8kdH+kdL+kdL+kdL+kcv8kKX2kGDqkmHtk2Lv"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEhohQWB5cKbRfLfnfLfng8Dvjc36kNH9kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+jsX3g5PgeV3Gcke4SC92CwcRRzB0j1/ii1zii1zijZPvkMT7kdL+kdL+kdL+kdH+kML6jpzyjm7qkF/okmDrlGDt"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADBIXOFJnbqLMfLfnfLjngr/ujMz5kNH9kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+jsP2govdeVrGWTiQFg8lglbOjFzijF3kjF3jjIDrkL75kdL+kdL+j8P5jp/xjX/pjGvljl3lkF7okl/qlGDt"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQgKLUJTY5K5fLfnfLfnf7vqi8v4kNH9kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+i7nygH7XLSFKRS5wlGLojV7mjV7mjV7li2fljZLvjqrzjqXyi4zqi27ji13fjFzijl3kkF7okl/qlGDt"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIzRBWoSmerPifLfnfbnohcPxkNH9kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kM/9gaffOUVhgFXLjl7mj1/ojl7njV7mjF3ki2rki3Tmim/iiGLdg1fSfVHFh1rakWDokl/pkmDrlGDt"
                + "AAAAAAAAAAAAAAAAAAAAAQAAAAAAERkfTHCOd7DefLfnfbjogL3sjc35kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+hL7rZ5TCUHKhRmOTQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OTm6eY469fbThkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+i8j0c5HGknLsjl7nj1/oj1/ojl7njV7ljFzii1vfilvfcEqzOSpSMSdANidTZEKhiVvZlGPslWLu"
                + "AgICBAMDBAMDAQEBAQEBAgEBBAYHOVNoapzFfLfnfLfnfrrqh8XzkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+eKzZWX+uSmmYRmKSRF+PQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OSmmYTGycSGWVQ16OQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16ORWGRSWeXWH2sd6zZkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+jr/1j5LwjmDnj1/oj1/oj1/ojl7njVzjjFvhi1zgYECZUEZLd2lpVElLJyIoMSRJakepkGHh"
                + "Pjg4ODExPjg4Mi0rLiooJiIhMThBY4+xfLfnfLfnfLjngb7tjs36kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "fbThWH2sR2STQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OUXSjbp7MfLLfa5nHTWycQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OR2STWX+ug77rkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdD+j7D2jW3oj1/oj1/oj1/okF7njl3ljmLli27kY1udZ254iYqUiYiQhH2BW1BSIh4iMSNM"
                + "9ufb9efa9ejc8uXZ7N/R5NrNqcHVhrnhfLfnfLfnfrrqhsTykdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "ZI+9SWeXQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OZJC+hL/skdL+g73qYYu5Q16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OSmiYaJXDkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+j8L5jpDvjV/mkF7nkF7okF7oj13mkH3sjq/zj835kdL+kc/6jcHlip+0i3t8hXV2T0ZI"
                + "3q+V4Lad5L6l5sSu58q23NDHkr7hfLfnfLfnfLfngb3si8v4kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "Tm+fRWGRQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16ORmOTdanXjcz4kdL+jcv4dajVRWGRQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16ORWKSUHKhkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdD+j7T2jmTlj13mkF7okF7okF7nkJ7zkMv7kdL+kdL+kdL+kdH9jcnyZlhafnBxjn5+"
                + "2aKH2aaK2KSJ1qWK06SJt6ilirfbfLfnfLfnfLfng8Duj9D8kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OUnWlgbvokdL+kdL+kdL+gbrnUHGgQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16ORF+PR2OTkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kcT7jo3tjl3lkF7nkF7nj13mkKL0kc78kdL+kdL+kdL+kND7jcXsZFZYVUlLaFxd"
                + "2aGG2KSJ16SI1aWJ0qSJmq/FgrfjfLfnfLfnfrrphsXzkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "RmOTRF+PQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16ORF+PR2STTGuaa5rIicXykdL+kdL+kdL+icbybJrIS2qZ"
                + "R2OTRF+PQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16ORWCQkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kMr8j6bzjVzjjl3lj13mj13mj37ugqThi8jxkdL+kND8jsTsjJ+zh3d3fG5vXlNV"
                + "2KGG16CG1qOI06OIxqOOirTXfrjnfLfnfLfngLzricj1kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "WH2sSGWVRF+PRF+PRF+PRF+PRF+PRF+PRF+PRF+PRF+PRF+PRF+PRWCQSmiXXIOyea/cjs36kdL+kdL+kdL+kdL+kdL+jcz5dKfU"
                + "W4KxSmiXRWCQRF+PRF+PRF+PRF+PRF+PRF+PRF+PRF+PRF+PRF+PRF+PRmKSTm+ekdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdD+kL/5jWHjjl3kjl3ljl3ljWPlXU2YTVVhhIiTZ214goSNhX2BiHh4gnR1b2Fi"
                + "15+E15+F1KCF0Z+EtaWhhLXdfLfnfLfnfLfngr/ujMv4kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "grzoZI+9V3yrVHinVHinVHinVHinVHinVHinVHinVHinVHinXYSzcaLQhsLujMr2j877kdL+kdL+kdL+kdL+kdL+kdL+kdH9js76"
                + "jMr2g73pcKDNXYSzVHinVHinVHinVHinVHinVHinVHinVHinVHinV3yrY4+9gbrnkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kMj7jHvojF/ijl3kjl3ki1zhRS9vQzs9fnBwXVJTgXJzhHV1bmBiWExOZllb"
                + "1ZuB1Z2D0pyC0J6DpaiygrXhfLfnfLfnfLfng8Hvjs77kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "j8/7h8TwhL/shL7rhL7rhL7rhL7rhL7rhL7rhL7rhL7rhL7rhsHui8n1kNH9kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+j8/7i8j1hsHuhL7rhL7rhL7rhL7rhL7rhL7rhL7rhL7rhL7rhL/sh8Twj8/7kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kMv8jZPtjWDijl3kjl3mh1raOihZWk9Qfm9vXVJSe2xtW1BRX1NUfG1ui3t7"
                + "1pp+1JyC0ZuBzZh/lqzCgbfjfLfnfLfnfLfnhcLxj9D8kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+h8PvgLnmfrbjfrbjfrbjfrbjfrbjfrbjfrbjfrbjfrbjhL/sjcz4kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+i8n1g73qfrbjfrbjfrbjfrbjfrbjfrbjfrbjfrbjfrbjgLnmhsPvkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kc38jqTxi2Hijl3kj17mhVnXOChTbF9gemxsVElLVUlLcGNkhXZ2int7fm9w"
                + "1pl90pd9z5Z9zJd+i6/SfrflfLfnfLfnfLfnhcPxkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "dqzZYIy6W4KxWoGwWoGwWoGwWoGwWoGwWoGwWoGwWoGwWoGwX4i3aZfFdqvYg77rjs76kdH+kdL+kdL+kdL+kdL+kdL+kdH+js76"
                + "g77qdqvYaJXDXoa1WoGwWoGwWoGwWoGwWoGwWoGwWoGwWoGwWoGwXIOyZJC+e7HekdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kc79jrT1i2Lhjl3kkF7ogFPRNShJeWpreGlqYFVVf3Fxhnd3h3d3WU9RGRUc"
                + "1pZ50pZ8zpV8yZJ5g7XffbjnfLfnfLfnfLfnhsTykdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "THKhPFmJP1qKQl2NQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OR2WUU3amY468c6bThsLvj8/7kdL+kdL+kdL+j876hb/scaPR"
                + "Y428U3amR2WUQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OR2SUWH2skdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdD+kMT5i2Phjl3kkF/pe1DHLCM4fG5uh3d3hXV1hnZ3a15fKyYpJRo1WzuU"
                + "1JR50JJ5zJF5yI93fLfnfLfnfLfnfLfnfLfnhsTykdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/OlaFQFuLQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16ORF+PSmmYbZ3LicfzkdL+kdL+kdL+iMXyapjGR2OT"
                + "Q1+PQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdH+kMr7i2PhjVzjkV/peU7FLCI4fXBxhXZ2c2VnPDM4IhouSC51cUa3fk3J"
                + "1ZB0z5J4y5B3zZuGfLfnfLfnfLfnfLfnfLfnhsPykdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/NFCAPFeHQl2NQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OUnSjgLjlkNH9kdL+kdH9gLnmUnSkQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdH+i2ThjVzjkV/pa0atJB0scGNjRj5BIRopPihha0GtfEzHgE/OjFvj"
                + "0450z5B3zJF53cGvfbfnfLfnfLfnfLfnfLfnhMLwj8/8kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/NVGBPFiIQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OcqTSjMr2kdL+js35dqnXQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdH+imTgjVzjkF/ofFLJMyNQIxssMyJPaEKoe0vFfk3Jh1XZk2DtlmLx"
                + "1Y5x0Y1x0qKL49LEfrXie7XlfLfnfLfnfLfnhMHwjs/8kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/N1KCPVmJQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OYo27g77rkdL+gbrmXYSzQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdH+imTgjVzjkF7okWDqi1zgeUzDg1TUhFPVg1PUjlzllmLxlmLxlmLx"
                + "1Y1u1JV56NTF0snAdKjSerTjfLfnfLfnfLfngr/ujMz5kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/NFCAN1KCPFiIQl2NQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OTW2dZpPBdKfUZI++SWaWQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdD+j8T5i2LgjVzjkF7okmDrlGHulmLwlWLwlmLxlmLxlmLxlmLxlmLxlmLx"
                + "1Y5u6c+/49XIZltXXYeqd7DefLfnfLfnfLfngb3sisn2kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/M09/NFCANlKCO1aGQV2MQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16ORGCQTW2cUnSjTGybQ16OQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kM/9jrT0imHfjVzjkF7okmDrlGHulWHvlWLwlmLxlmLxlmLxlmLxlmLxlmLx"
                + "4bSb6t3QcGZiEA4OS26LdazafLfnfLfnfLfnf7vqiMb0kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/M09/M09/M09/NVGAOlaGP1qKQl2NQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQVyMPViIkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kc38jKLuimDfjVzjkF7okmDrlGHulWHvlWLwlmLxlmLxlmLxlmLxlmLxlmLx"
                + "6d3WbmBgDQoLAAAAOFNpcqjVfLfnfLfnfLfnfbjohcLxkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/M09/M09/M09/M09/NFCAOFODPViIQVyMQ16OQ16OQ16OQ16OQ16OQ16OSGaVUHKhVHinUnSjS2mZQ16OQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQl2NP1qKO1aGNlGBkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kMr7i47pil/fjl3kkV/okmDrlGHulWHvlWLwlmLxlmLxlmLxlmLxlmLxlmLx"
                + "V0xLBgYGAAAAAAAAGygzbqLNfLfnfLfnfLfnfLfngr/tjc36kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NVGBOlaGP1qKQVyMQl2NQ16OQ16OSmiYXYW0cqTRe7HedajVY428SmiYQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQ16OQl2NQFuLPFiIOFODNFCAM09/kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kMf6iHXiilzejl3kkV/ok2DslGHulWLwlWLwlmLxlmLxlmLxlmLxlmLxlmLx"
                + "BAMDAAAAAAAAAAAABQcJYI2yebLhfLfnfLfnfLfnf7vriMf1kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NVGBOlWFP1qKQl2NQl2NY468hsLvkdL+kdL+kdL+h8PvZI+9Q16O"
                + "Q16OQ16OQ16OQ16OQ16OQ16OQ16OQl2NQl2NQVyMPFeHN1KCNFCAM09/M09/M09/kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kc/9j7z3h2Hci1vfjl3lkV/pk2DslGHulWLwlWLwlmLxlmLxlmLxlmLxlmLxlWLw"
                + "AAAAAAAAAAAAAAAAAAAAR2mEcqjUfLfnfLfnfLfnfbnohMHwkND8kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NFCANlKCOlWFfLPgjs76kdL+kdL+kdL+j9D8gbvoQ16O"
                + "Q16OQ16OQ16OQ16OQ16OQl2NQFuLPFeHOFODNFCAM09/M09/M09/M09/M09/M09/kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kMn7jaDtiFnai1vgj13mkV/plGDtlWHvlWLwlmLxlmLxlmLxlmLxlmLxlWLwlWLw"
                + "AAAAAAAAAAAAAAAAAAAAKz9PaZrDe7bmfLfnfLfnfLfngLzri8r3kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NFCAf7nlj9D8kdL+kdL+kdL+kdL+hcHuPlmJ"
                + "PViIPFeHO1aGOlWFOFSENlKCNVGBNFCANFCAM09/M09/M09/M09/M09/M09/M09/kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+j8D3iHnhiVrcjVzikF7nkl/qlGDtlWHvlWLwlmLxlmLxlmLxlmLxlWLwlWLwlWPy"
                + "AQAAAAAAAAAAAAAAAAAABgkLVXydd6/dfLfnfLfnfLfnfbnog8DvkNH9kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "N1WFNFCAM09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/darYjMv4kdL+kdL+kdL+isn1b6PQNlKB"
                + "NlGBNVGBNVGBNVCANFCAM09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdD9jrb0iGTcilrdjVzjkF7okmDrlGHulWHvlWLwlmLxlmLxlmLxlmLxlWLwlWLxlWPy"
                + "AgIBAAAAAAAAAAAAAAAAAAAALUJUZpa+fLfnfLfnfLfnfLfnf7rqjMv4kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "Q2WVNlODM09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/UHamcqfVicf0kdL+jMv3d63bTnWkM09/"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NVKCQWKSkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+j8L4iZDoh1jZi1vfjl3lkV/pk2DslGHulWLwlWLwlmLxlmLxlWLwlWLwlmLxlWPyk2Px"
                + "DwoIAQEBAAAAAAAAAAAAAAAACAwPUXeWebLhfLfnfLfnfLfnfLjogL3skNH9kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "YI28O1qKM09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NlODTHKhYY+9aZrIY5LAUXmoOVeHM09/"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/OlmJW4a1kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+jLHyhmTaiFnbjFvhkF7nkl/qlGDtlWHvlWLwlWLwlmLxlmLxlWLwlWLwlWLxlWTylGPx"
                + "jVxIDQkHAQEBAAAAAAAAAAAAAQECLkRWZJS7fLfnfLfnfLfnfLfnfbjohsXykND9kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "gbzpTnWkOFaFM09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NlKCN1SENlKCM09/M09/M09/"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/OFWFTHKhebHfkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+jrz2iYblh1rZilrejl3kkF7okmDrlGHulWHvlWLwlmLxlmLxlWLwlWLwlWLxlWPylGTylWTy"
                + "0IVtbUtACgcHAAAAAAAAAAAAAAAACQ4RQmJ7cqnVfLfnfLfnfLfnfLfnfrrpicf1j9D8kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+cqjVTHKhOlmJNVKCM09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NFCANlODO1uLTnSjcqfUkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kMv7i53shWDZiFnbi1vgj13mkV/pk2DslGHulWLwlWLwlmLxlmLxlWLwlmLxlWPylWTyk2LwlGbz"
                + "0YNvxoZxNywqBQUFAAAAAAAAAAAAAAAAGCQtTnSSebLhfLfnfLfnfLfnfLfnf7vricj1j9D8kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+eK/dWYS0QGGRM09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/"
                + "M09/M09/M09/M09/M09/M09/M09/M09/M09/M09/NlSEQmWUXoy6gLvpkdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kc/9jKbthnDch1jYilrejVzjkF7okl/qlGDtlWHvlWLwlmLxlmLxlWLwlWLwlmLxlWPylWTzj1/pk2Tx"
                + "04Vw1IlzuZiOGRUVAQEBAAAAAAAAAAAAAwUHJzlIXoqufLfnfLfnfLfnfLfnfLfngLzricj1j9D8kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+jbHyh3zghVrXiFnbjFvhj13mkV/pk2DslGHulWHvlWLwlmLxlmLxlWLwlWLxlGTylWPxlGXxlGTylGbx"
                + "1Idy04l0zpB7UUVABAMDAAAAAAAAAAAAAAAACxAUMUlcZZa9fLfnfLfnfLfnfLfnfLfngL3siMb0js77kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+jrz2h4bjhWDYh1jZi1vfjl3kkF7okmDrlGDtlWHvlWLwlmLxlmLxlWLwlWLwlWPxlGXxlGPxlGXxlGTxlGby"
                + "1It104t22JiAinNuDw0NAAAAAAAAAAAAAAAAAAAADhQZNk9jZ5jAfLfnfLfnfLfnfLfnfLfngLzriMf0j9D8kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+jr32iIvkhWXYhljXilrdjVzij13mkV/pk2DslGHulWHvlWLwlmLxlmLwlWLwlWLwlGXylWPxlGTylGXxlGTxlGby"
                + "1ZF51ZF51pV90byuIyEhAQEBAAAAAAAAAAAAAAAAAAAADBEVNExgY5K4ebPifLfmfLfnfLfnfLfngb7ti8r4kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kND9jrv2h4vjhWPXhljXiVrcjVzij17mkl/pkmDrlGDtlWHvlWLwlmLxlmLxlWLwlWLwlGXxlGXylGXylGTxlGXxlGTxlGbx"
                + "1pR715d91ZV83LqqSUNDAgICAAAAAAAAAAAAAAAAAAAAAAAABQgKJDRCYI6zebLhfLfnfLfnfLfnfbnohcTykNH+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdH+"
                + "kMr7jbPxhn7fhV7VhlfXiFnbi1vgjl3kkF7okl/qlGDtlmLwlWLwlWLwlmLxlmLxlWLwlWTxlWTxlmTylGXylGbylGTxlGfylmfy"
                + "15d915qA2JuB4baibWFhBQUFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgMEGicxap3GerXkfLfnfLfnfLfng8Dvjs/7kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdH+kM/9j8T4"
                + "i6Xsg2LUg1fShlfXiFnajFzgjVzjkF7nkl/qk2DslWLulWHvlmPxlmLxlmLxlWLwlmPxlmPxlWXylWXylGXxlGbwlGXwlWfylmbx"
                + "2JyC2JyC2J6D4baffHFvBwYGAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAPFhwcKXRfLfnfLfnfLfngb7ti8v4kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdD+kMz7jbbzh4Xg"
                + "glXQhFfUhljXiVrcjFzgjl3kkF/okmDqlGHtlWLvlmLwlmPxlmPxl2PylmLxlmPxlWTxlWTxl2XxlmbylGTxlGXxlWbylWfylWjy"
                + "2JyC2qGG2aCG58Csg3t4CAcHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFiEqZ5jAfLfnfLfnfLfngb7ti8v4kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+jcz3jLLwh4Pgg2HUhFrT"
                + "hlnViFrYilzcjV3hkF/lkWHok2LqlWPtlWPvl2TwlmTxl2Txl2Tyl2Tyl2Tyl2Txl2XymGbyl2Xxj2DolGXwlWXxmGfymGfyk2jx"
                + "2qGG2qKH2qKH7tC+kIqHCQgIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwQFYpG3fLfnfLfnfLfngLzsisn2kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+i7rwdJXJUl6OhWfUhF3UhlrViFvX"
                + "iVvbjF7fjl/ikWDmkmLplGPrlmTulmTvl2Xwl2bxl2bxmGbymGbymGbymGXymGXymGfzmGfzlWbxg1bTlmTvl2fymWfzlmfylGny"
                + "2qKH26aK3KiM9NnFjoN/CAcIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAYpC2fLfnfLfnfLfngLzricj2kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kdL+kM/8jsn4kcX0krvslbPmjZ/cdm3CdWDBQjpvSTl5hFvVh1vXiFzZi17d"
                + "jV/gj2HkkWLnk2PplWTslmXul2bwmGbxmGfymGfymWfzmWfzmWfzmGXymGfzl2fymWfzmWfzl2fvfk7Lk2HqmGfzmGj0lmnylmny"
                + "26aK26eL3auQ9d/PjYF9CAcHAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAYpC2fLfnfLfnfLfngLzricj2kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+kdL+kdL+js35frfdV3+ZNk5eL0RSLkJQLkJQLkJQLkJQLkJQLkJQLkJQLkJQLkJQLkJQLkJQLkJQLkJQ"
                + "LkJQL0NROElhenbLg33cg33cg33cg33cgn3bg33aeXLJd2/DhoDKm5PPmo3LmYjJdVy5bEWuc0m3Lx9OUziJiFrai13djF/fjmDi"
                + "kWHmkmLolGPqlWTslmXul2bwmGbxmGfymGfymWfzmWfzmWfzmWbzmWbzmWfzmWfzmWfzmWjzkF/nfUzJh1jam2r1m2v2m232mm3y"
                + "26eL26eL37KX9ubYf3d2BwYGAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAX4uwe7XkfLfnfLfngLzricj2kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+kdL+j8/6f7jeW4SgNUxdGSQsBQcIAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAABQQJUTqCgFvSf1rTgFrSgVvSgVvSgFvSeVLFbUOuelKznnzAoX/Bon/DimS7bkWxfFa4i2XHKx1GXj+YjF7hj2LkkWPmkmTo"
                + "lGXqlWXslmful2XxmGjxmGbxmGfzmGf0mWfzmWf1mWjzmWfzmWfzmWfzmmj0mmn0m2r0mGnzjFvhfE3IgFHPimLda0qrUzqHMiRS"
                + "5r2n4bSa4rWb9+fZdGpqBQQFAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAU3qadq7cfLfnfLfngLzricj2kdL+kdL+kdL+"
                + "kdL+kdL+kdL+kdL+h8TtaZi4P1tvHCkyBAYHAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AgEDHBUvgF/Qgl3YhF3ZhF3YhV3YhV3YhF3Ydky+c0m0lW/CpoLHpYHHnXfHfFS6k2zDpYDNhFrFKRtDZUKhkGDok2XolGXplmft"
                + "l2ftmGnvmGfwmmrzmWjymWnzmWnzmmn0m2r0mmn0m2r0m2r0mWjzmmj0m2r0m2r0m2v1mmrziVvdflHKb0e0JxtBDgoXBgQKAgIE"
                + "w7257eHX897P9OneYVlZAwMDAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP112b6POfLfnfLfnf7vriMb0kdL+kdL+kdL+"
                + "kdL+kdL+jMr0cqXIUXWOKTtIBwoMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "BQQJVD+Limbih2Hfh2Hgh2HfiWDfiWDfh13Zdku8iWLAqYTMqYXMqoXNmnXKoXvNrojQnHXMfE7FKhtEaUanlWnpl2jtl2ftmGjv"
                + "mWjxmWrxm2rzm2v0mmrzmmrzm2r0nGz1mmn0m2v1m2r0m2r0m2r0m2r0m2r0m2v0m2v1mmvzi2DgfVDKfVDKRy90CAUNAAAAAAAA"
                + "JSIiZmRltLGt3dzbSUZHAgICAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAITE+XIiseLHffLfnfbnphMLxkNH9kdL+kdL+"
                + "js75e7LYYIqnMEVTDBEUAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQEC"
                + "Ew0fgmHRi2Tni2PmjGPmjGPljGPljWTmhFnXfFHApX7Or4nSronSronSrojTsIrUqoPTglfGVTeLEgweakepl2rtmWrwmmrxmWry"
                + "m2vzm2r0m2z1nGz2nGz1nWz3nGz1nGz1nWz2nGz1nGz1nGz1nGz1nGz1nGz1nG31nGz1m2z0i2Lgf1TMfFTJi17dRTJvBwQLAAAB"
                + "AAAAAwMDEBAQVk9PKSYnAgEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAMkpdYI2ydq7cfLfngL3si8r4jcz3gr3k"
                + "bZ6/O1VoDRMXAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAMG"
                + "MyRVkmzuj2fsj2brkGbrkWbqkWfskWfsglfRlWrMsovVsozWsozWs4zXs4zXsozXjGXIUjSEDgkWBQMHa0mrmWnzmmzzm270m2zz"
                + "nGz3nG31n236nWz3nm33nW72nW72nm34nW32nW/3n236nnD3nW72nW72nnD3nW/3nW72nG71jmbmf1TMgVbNhlrPmXTsQjBqBwUM"
                + "AAAAAQEBBQQEAQEBAQAAAQAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJDVDVHydbaHMcKTPcabPb6HDRWR6"
                + "DRMXAQICAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAYN"
                + "a1CulWzykmnwk2nxlGrylGnxlGnxl2rvh1vSrIPYt5DbtY7atY7ato/bt5HcmnbPQixrBwULAAAABgUIb0+xm27ynmz3jGjim2/0"
                + "nXH3nnH4nnD4nnD4nm72nnH4n274n3D3onD6oHL3o3D7oHT4nnH3nnH4nnL4nnH4nnP2nnL3kmjrf1TMf1TLgVXNlm7rn3rtQi9p"
                + "AAAAAAAAAAAAAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwUHERkfHSo1FB0lBgoMAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADAkU"
                + "h2XWlWv2l230lGz0mW30l2v0lmv1lGnxmHDZt5Dct5Dct5Dct5DcuJLcqYnaOClVAwIGAAAAAAAAAQEBblGwnXPznHX0cU+zmXDq"
                + "onL6n3P4n3L4n3L4n3L4n3L4oHD4oHP4oHT4oXT4oXX4nm/2n3L4oXX4oHP3oHT4oHT4oXX3mGvvelbFbEmxg1jOkmncoXb4pIDw"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQECHhUy"
                + "k2/ulmz4lm33mW73m231mWz2mW33l2vvrIPdupLeupLeuZHeuZLespDYQzhZAwIFAAAAAAAAAAAAAQABZEafoHb4n3j2Vz6Kkmzh"
                + "oHX4oHT4oHX4oXb4oXb4oXb4oHT4oXb4onX5o3X5onb4onf4oXX3onf4onf4onb4oXb4oXf3m3HxhFzMXT6YhVvOhVzRoHn2oXj2"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwIFMCJP"
                + "mHT8l3D4mG7ymW/4mm73nHD3nHH3n3bzto7huZHfupLfupLguJTeY1J/BgUJAAAAAAAAAAAAAAAAAAABVz6Ln3j2o3z8TTh7hGDN"
                + "onj6onf4onf4onf4onf4pXX7onf4o3b4o3X5onf4onf4pXX7onf4onf4onf4onf4o3n4o3n4oHjyimLSSzV6eVW8g13On3jso335"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwIGNihb"
                + "nHL8hmXaQCxonnX5nHL6nnH4nXH4sY/3upTiupLgu5PgvJXiinGvDw4TAAAAAAAAAAAAAAAAAAAAAAABTjp7oX3xpHz7W0KOeVm7"
                + "onr5o3r4o3r4pHr4pHr4o3n4pX33m27pmG/lpHz5pXz5p33zpn34pHv4pX34pX34pX34pX34onnziWHPUzmFZkeiiWDRlW3gpX73"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAwIFMSNS"
                + "nHf6Y0icGRErn3b5nHL5m3P7nHT6waT5u5bjupLgvJTiq4nRNCpEAQABAAAAAAAAAAAAAAAAAAAAAAAAQjFio3/vpn74fF60aU2f"
                + "onv2pX76pn74p3v6pn/4pn35poD3k2vgjGLXpoH4pn/4pn/4pn/4pn/4pn/4pn/4pn/4pn/4pYD1jGPUcVC2RC5wjmXTi2bVp4L4"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAgEDIxg5"
                + "mHTtOytgGBApn3n8nnP5oHb5onj40Lv7v5nlupLgvJbjeF+VCAYKAAAAAAAAAAAAAAAAAAAAAAAAAAAANidSpIDupoD4k3PaWkaA"
                + "nnvspoD4poD4qIP5poD4poD4p4L4k2vfkGfXpoD3p4H5p4H5qIH2poD4p4H5p4L5qIP5qIP5p4P4l3Dei2XOLyFLg17Ii2LSoHvw"
                ;
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            byte[] raw = Convert.FromBase64String(TplB64());   // BGR 交错
            for (int i = 0; i < TW * TH; i++)
            {
                _tB[i] = raw[i * 3]; _tG[i] = raw[i * 3 + 1]; _tR[i] = raw[i * 3 + 2];
                // 掩码：只比对「气泡本体」像素（米橙气泡 + 棕色宝箱），忽略背景。
                // 宝箱随桌宠移动，窗口背景（头发/身体/场景）每次都不同——
                // 全像素 MAD 会因背景差异高达 35+，这就是旧版「宝箱不被识别」的根因。
                int b = _tB[i], g = _tG[i], r = _tR[i];
                bool cream = r > 190 && g > 130 && g < 235 && b > 60 && b < 185 && r > g + 15 && g > b + 30;
                bool brown = r > 90 && r < 210 && g > 55 && g < 160 && b > 25 && b < 120 && r > g + 20 && g > b + 15;
                _tMask[i] = cream || brown;
            }
            _loaded = true;
        }

        /// <summary>
        /// 在一帧 BGRA 位图中找所有宝箱。返回 (中心x, 中心y, MAD) 列表（帧内坐标）。
        /// </summary>
        public static List<(double Cx, double Cy, double Mad)> Find(byte[] bits, int w, int h)
        {
            EnsureLoaded();
            var result = new List<(double, double, double)>();
            if (bits == null || bits.Length < w * h * 4) return result;

            // ---- 1) 橙色签名掩码（step 4 采样）+ 4 连通聚类
            var cells = new HashSet<(int, int)>();
            for (int y = 0; y + 3 < h; y += 4)
            {
                int row = y * w * 4;
                for (int x = 0; x + 3 < w; x += 4)
                {
                    int i = row + x * 4;
                    int b = bits[i], g = bits[i + 1], r = bits[i + 2];
                    if (r > 190 && g > 130 && g < 235 && b > 60 && b < 185 && r > g + 15 && g > b + 30)
                        cells.Add((x, y));
                }
            }
            if (cells.Count == 0) return result;

            var seen = new HashSet<(int, int)>();
            foreach (var p in cells)
            {
                if (seen.Contains(p)) continue;
                var comp = new List<(int, int)>();
                var stack = new Stack<(int, int)>();
                stack.Push(p); seen.Add(p);
                int x0 = p.Item1, y0 = p.Item2, x1 = p.Item1, y1 = p.Item2;
                while (stack.Count > 0)
                {
                    var c = stack.Pop(); comp.Add(c);
                    if (c.Item1 < x0) x0 = c.Item1;
                    if (c.Item1 > x1) x1 = c.Item1;
                    if (c.Item2 < y0) y0 = c.Item2;
                    if (c.Item2 > y1) y1 = c.Item2;
                    for (int dx = -4; dx <= 4; dx += 4)
                        for (int dy = -4; dy <= 4; dy += 4)
                        {
                            var n = (c.Item1 + dx, c.Item2 + dy);
                            if (n.Item1 != c.Item1 && n.Item2 != c.Item2) continue;  // 4 连通
                            if (cells.Contains(n) && !seen.Contains(n)) { seen.Add(n); stack.Push(n); }
                        }
                }
                // 宝箱气泡面积 ~2700 物理px（step4 下 N≈168）；预筛下限放宽到 40
                if (comp.Count < 40) continue;

                // ---- 2) 模板匹配确认：在簇 bbox 外扩 34px 的窗口内滑窗
                int sx0 = Math.Max(0, x0 - 34), sy0 = Math.Max(0, y0 - 34);
                int sx1 = Math.Min(w - TW, x1 + 34), sy1 = Math.Min(h - TH, y1 + 34);
                if (sx1 < sx0 || sy1 < sy0) continue;
                double bestMad = double.MaxValue; int bx = -1, by = -1;
                for (int yy = sy0; yy <= sy1; yy += 1)
                    for (int xx = sx0; xx <= sx1; xx += 1)
                    {
                        double mad = MadAt(bits, w, xx, yy);
                        if (mad < bestMad) { bestMad = mad; bx = xx; by = yy; }
                    }
                if (bestMad < MadThreshold)
                {
                    // 去重：两个宝箱挨得近时簇搜索窗互相重叠，可能滑到同一个位置
                    double cx = bx + TW / 2.0, cy = by + TH / 2.0;
                    if (!result.Exists(r => Math.Abs(r.Item1 - cx) < 50 && Math.Abs(r.Item2 - cy) < 50))
                        result.Add((cx, cy, bestMad));
                }
            }
            return result;
        }

        /// <summary>模板 MAD（隔 3 像素采样加速）</summary>
        static double MadAt(byte[] bits, int w, int px, int py) => MadAtPublic(bits, w, px, py);

        /// <summary>诊断用公开入口</summary>
        internal static double MadAtPublic(byte[] bits, int w, int px, int py)
        {
            long sum = 0; int n = 0;
            for (int ty = 0; ty < TH; ty += 3)
            {
                int row = (py + ty) * w * 4 + px * 4;
                int trow = ty * TW;
                for (int tx = 0; tx < TW; tx += 3)
                {
                    int t = trow + tx;
                    if (!_tMask[t]) continue;   // 只比对气泡本体，忽略背景像素
                    int i = row + tx * 4;
                    int db = bits[i] - _tB[t];
                    int dg = bits[i + 1] - _tG[t];
                    int dr = bits[i + 2] - _tR[t];
                    sum += (db < 0 ? -db : db) + (dg < 0 ? -dg : dg) + (dr < 0 ? -dr : dr);
                    n++;
                }
            }
            // 掩码像素太少（如窗口大部分悬空在画面外/落在纯色上）不可信
            return n < 350 ? double.MaxValue : sum / (n * 3.0);
        }
    }
}
