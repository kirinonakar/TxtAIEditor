using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TxtAIEditor.Core.Services.LLM
{
    internal static class LlmTokenUsageHtmlReportService
    {
        private const string TemplateFileName = "llm-token-usage-dashboard.html";
        private const string DataPlaceholder = "__TOKEN_USAGE_REPORT_DATA__";
        private const string DefaultStringsJson = """
            {"title":"Token 사용 현황","subtitle":"요청량, 토큰 소비, 캐시 효율을 한눈에 확인하는 누적 대시보드","periodBadge":"표시 기간","themeToggle":"테마 전환","darkModeSwitch":"다크 모드로 전환","lightModeSwitch":"라이트 모드로 전환","summaryAria":"핵심 통계","totalRequests":"총 요청","dailyAverageFormat":"일평균 {0}","totalTokens":"전체 토큰","averagePerRequestFormat":"요청당 평균 {0}","cachedTokens":"캐시된 토큰","cachedTokensNote":"입력 토큰 재사용량","cacheRatio":"캐시 비율","cacheGood":"효율 양호 · 목표 70% 이상","cacheNeedsImprovement":"개선 권장 · 목표 70% 이상","dailyHeading":"일별 사용 추이","dailyDescription":"날짜별 요청량과 토큰 소비 패턴을 비교합니다.","dailyInsightsAria":"일별 주요 인사이트","peakRequests":"최다 요청","peakTokens":"최다 토큰","requestCount":"요청 수","dailyRequestsDescription":"하루 동안 발생한 전체 모델 요청","countUnit":"회","tokenUsage":"토큰 사용량","dailyTokensDescription":"입력과 출력을 합산한 일별 소비량","millionUnit":"백만","inputOutput":"입력과 출력","inputOutputDescription":"입력 토큰과 출력 토큰의 일별 구성","input":"입력","output":"출력","cacheEfficiency":"캐시 효율","cacheEfficiencyDescription":"70% 이상은 안정적인 재사용 구간으로 표시","modelHeading":"모델별 사용 분석","modelDescription":"모델별 요청 빈도와 토큰 소비 편중을 비교합니다.","modelInsightsAria":"모델 주요 인사이트","mostUsedModel":"최다 사용 모델","requestShare":"요청 비중","modelRequests":"모델별 요청 수","modelRequestsDescription":"요청 기준 상위 8개 모델","modelTokens":"모델별 전체 토큰","modelTokensDescription":"1천 토큰 이상 사용한 모델","modelInputOutput":"모델별 입력과 출력","modelInputOutputDescription":"토큰 사용량 상위 10개 모델의 입력·출력 구성","horizontalAxisNote":"가로축: 백만 토큰","noDataTitle":"표시할 토큰 통계가 없습니다","noDataDescription":"LLM 요청이 기록되면 이 보고서에 자동으로 반영됩니다.","footerPeriodFormat":"표시 기간 {0} · 누적 기준","footerProduct":"TxtAIEditor Token 통계 · Chart.js 기반 정적 대시보드","generatedAtFormat":"생성: {0}","requestsFormat":"{0}회","tokensFormat":"{0} 토큰"}
            """;

        public static string BuildHtml(
            LlmTokenUsageStats stats,
            Func<string, string, string> getString)
        {
            string templatePath = Path.Combine(PreviewWebResourceService.WebResourcesPath, TemplateFileName);
            string template = File.ReadAllText(templatePath, Encoding.UTF8);
            if (!template.Contains(DataPlaceholder, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Missing report data placeholder in {TemplateFileName}.");
            }

            var report = new
            {
                Culture = getString("TokenReportLocale", "ko-KR"),
                GeneratedAt = DateTimeOffset.Now,
                Stats = stats,
                Strings = CreateLocalizedStrings(getString)
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            return template.Replace(DataPlaceholder, json, StringComparison.Ordinal);
        }

        private static IReadOnlyDictionary<string, string> CreateLocalizedStrings(Func<string, string, string> getString)
        {
            string localizedJson = getString("TokenReportStringsJson", DefaultStringsJson);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(localizedJson)
                    ?? JsonSerializer.Deserialize<Dictionary<string, string>>(DefaultStringsJson)!;
            }
            catch (JsonException)
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(DefaultStringsJson)!;
            }
        }
    }
}
