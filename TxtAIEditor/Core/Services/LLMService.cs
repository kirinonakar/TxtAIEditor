using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Core.Services
{
    public class LLMService : ILLMService
    {
        private readonly ISettingsService _settingsService;
        private readonly ICredentialService _credentialService;
        private readonly ILocalizationService _localizationService;
        private readonly object _tokenUsageStatsLock = new();
        private readonly Dictionary<string, MutableTokenUsageBucket> _tokenUsageBuckets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MutableTokenUsagePeriodBucket> _tokenUsageDayBuckets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MutableTokenUsagePeriodBucket> _tokenUsageMonthBuckets = new(StringComparer.OrdinalIgnoreCase);
        private LlmTokenUsageStats _tokenUsageStats = new();
        private LlmTokenUsage? _lastTokenUsage;

        public LLMService(ISettingsService settingsService, ICredentialService credentialService, ILocalizationService localizationService)
        {
            _settingsService = settingsService;
            _credentialService = credentialService;
            _localizationService = localizationService;
            LoadTokenUsageStats();
        }

        public LlmTokenUsage? LastTokenUsage
        {
            get
            {
                lock (_tokenUsageStatsLock)
                {
                    return _lastTokenUsage;
                }
            }
        }

        public LlmTokenUsageStats TokenUsageStats
        {
            get
            {
                lock (_tokenUsageStatsLock)
                {
                    return _tokenUsageStats;
                }
            }
        }

        public void ResetTokenUsageStats()
        {
            lock (_tokenUsageStatsLock)
            {
                _lastTokenUsage = null;
                _tokenUsageBuckets.Clear();
                _tokenUsageDayBuckets.Clear();
                _tokenUsageMonthBuckets.Clear();
                _tokenUsageStats = new LlmTokenUsageStats();
            }

            DeleteTokenUsageStatsFile();
        }

        private string GetActiveLanguage()
        {
            return GetActiveLanguage(_settingsService?.CurrentSettings);
        }

        private static string GetActiveLanguage(EditorSettings? settings)
        {
            var lang = settings?.Language;
            if (string.IsNullOrEmpty(lang) || lang.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
                }
                catch
                {
                    lang = "en-US";
                }
            }

            if (lang != null)
            {
                if (lang.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "ko-KR";
                if (lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja-JP";
                if (IsTraditionalChinese(lang)) return "zh-Hant";
                if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
            }
            return "en-US";
        }

        private static bool IsTraditionalChinese(string language)
        {
            return language.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> ExplainCodeAsync(string code, string language, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default)
        {
            string langCode = GetActiveLanguage();
            string systemPrompt = langCode switch
            {
                "ja-JP" => "あなたは正確な開発ドキュメント解説者です。ユーザーが提供した選択範囲のみを根拠に、日本語で説明します。選択範囲がコードの場合、動作フロー、主要な識別子・関数、入力と出力、副作用、潜在的なバグの可能性について説明します。選択範囲がMarkdown、一般テキスト、設定ファイルなどの場合は、その構造と意味を説明します。存在しない周辺コードやプロジェクトの意図を推測せず、不確実な部分は『選択範囲だけでは確認不可』と明記してください。原文をそのまま繰り返すのではなく、要点を整理します。",
                "zh-Hans" => "你是严谨的开发文档讲解者。只根据用户提供的选区内容，用简体中文说明。若选区是代码，请说明执行流程、主要标识符/函数、输入与输出、副作用以及潜在问题。若选区是 Markdown、普通文本或配置文件，请说明其结构和含义。不要推测选区之外的周边代码或项目意图；不确定处请明确写出“仅凭选区无法确认”。不要逐字重复原文，而要整理要点。",
                "zh-Hant" => "你是嚴謹的開發文件講解者。只根據使用者提供的選取內容，用繁體中文說明。若選取內容是程式碼，請說明執行流程、主要識別項/函式、輸入與輸出、副作用以及潛在問題。若選取內容是 Markdown、一般文字或設定檔，請說明其結構和含義。不要推測選取內容之外的周邊程式碼或專案意圖；不確定處請明確寫出「僅憑選取內容無法確認」。不要逐字重複原文，而要整理重點。",
                "en-US" => "You are an accurate developer documentation explainer. Explain in English, strictly grounding your explanations in the provided text selection. If the selection is code, explain the execution flow, primary identifiers/functions, input and output, side effects, and potential bugs. If the selection is Markdown, plain text, or configuration, explain its structure and meaning. Do not speculate about surrounding code or project intent that is absent, and explicitly write 'cannot be verified from the selection alone' for any uncertainties. Keep it concise without repeating the source text verbatim.",
                _ => "당신은 정확한 개발 문서 해설자입니다. 사용자가 제공한 선택 영역만 근거로 삼아 한글로 설명합니다. 선택 영역이 코드이면 동작 흐름, 주요 식별자/함수, 입력과 출력, 부작용, 주의할 버그 가능성을 설명합니다. 선택 영역이 마크다운/일반 텍스트/설정 파일이면 구조와 의미를 설명합니다. 존재하지 않는 주변 코드나 프로젝트 의도를 추측하지 말고, 불확실한 부분은 '선택 영역만으로는 확인할 수 없음'이라고 명시합니다. 원문을 통째로 반복하지 말고 핵심을 정리합니다."
            };

            string userContent = langCode switch
            {
                "ja-JP" => $"[選択範囲の言語またはファイルタイプ]\n{language}\n\n[選択範囲]\n{code}",
                "zh-Hans" => $"[选区语言或文件类型]\n{language}\n\n[选区]\n{code}",
                "zh-Hant" => $"[選取內容語言或檔案類型]\n{language}\n\n[選取內容]\n{code}",
                "en-US" => $"[Selection Language or File Type]\n{language}\n\n[Selection]\n{code}",
                _ => $"[선택 영역 언어 또는 파일 유형]\n{language}\n\n[선택 영역]\n{code}"
            };

            return await ExecuteLlmAsync(systemPrompt, userContent, onChunk, cancellationToken);
        }

        public async Task<string> SummarizeTextAsync(string text, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default)
        {
            string langCode = GetActiveLanguage();
            string systemPrompt = langCode switch
            {
                "ja-JP" => "あなたは正確な要約のスペシャリストです。ユーザーが提供した選択範囲のみを要約します。絶対に他の言語に翻訳しないでください。入力テキストが英語（English）なら必ず英語で要約し、日本語（Japanese）なら必ず日本語で、韓国語（Korean）なら必ず韓国語で要約してください。要約の出力言語は、入力テキストの元の言語と100%同一である必要があります。主要な主張、目的、結論、ToDoリストを簡潔に整理し、コードの場合は実装の意図と主要な処理ステップのみを要約します。原文にない内容は絶対に追加しないでください。挨拶、導入説明、要約の結果を示すラベル（例：『以下は要約結果です』）などの余計なテキストを一切含めず、純粋な要約コンテンツだけを直接出力してください。",
                "zh-Hans" => "你是严谨的摘要专家。只总结用户提供的选区。绝对不要翻译成其他语言。如果输入文本是英语，就必须用英语总结；如果是简体中文或繁体中文，就必须用相同中文书写形式总结；如果是日语或韩语，也必须使用原语言总结。摘要输出语言必须与输入选区的原语言 100% 一致。请简洁整理主要论点、目的、结论和待办事项；若选区是代码，只总结实现意图和主要处理步骤。不要加入原文没有的内容。不要包含问候、开场说明、元评论或“以下是摘要”等标签，只直接输出最终摘要正文。",
                "zh-Hant" => "你是嚴謹的摘要專家。只總結使用者提供的選取內容。絕對不要翻譯成其他語言。如果輸入文字是英文，就必須用英文總結；如果是簡體中文或繁體中文，就必須用相同中文書寫形式總結；如果是日文或韓文，也必須使用原語言總結。摘要輸出語言必須與輸入選取內容的原語言 100% 一致。請簡潔整理主要論點、目的、結論和待辦事項；若選取內容是程式碼，只總結實作意圖和主要處理步驟。不要加入原文沒有的內容。不要包含問候、開場說明、元評論或「以下是摘要」等標籤，只直接輸出最終摘要正文。",
                "en-US" => "You are an accurate summarization expert. Summarize only the provided selection. Do NOT translate to any other language. If the input text is in English, you must summarize in English. If it is in Japanese, summarize in Japanese. If it is in Korean, summarize in Korean. The summary's output language must be 100% identical to the original language of the input selection. Summarize the key arguments, purposes, conclusions, and action items concisely. If the selection is code, summarize only the implementation intent and major steps. Do not introduce any details not explicitly mentioned in the source. Do not include any greetings, introductory phrases, meta-commentary, or surrounding labels (e.g., 'Here is the summary:'). Output ONLY the final summarized text directly.",
                _ => "당신은 정확한 요약 전문가입니다. 사용자가 제공한 선택 영역만 요약합니다. 절대로 다른 언어로 번역하지 마십시오. 만약 입력 텍스트가 영어(English)라면 반드시 영어로 요약하고, 일본어(日本語)라면 일본어로 요약하며, 한국어(Korean)라면 한국어로 요약해야 합니다. 즉, 요약 결과는 반드시 입력 텍스트의 원래 언어와 100% 동일한 언어여야 합니다. 핵심 주장/목적/결론/할 일을 간결하게 정리하고, 코드인 경우에는 구현 의도와 주요 처리 단계만 요약합니다. 원문에 없는 내용은 절대 추가하지 마십시오. 인사말, 도입부, 부가 설명, 혹은 '요약 결과입니다:'와 같은 불필요한 메타 안내 문구를 단 한 자도 출력하지 마십시오. 오직 정제된 핵심 요약 본문만 직접적으로 출력해 주십시오."
            };

            string userContent = langCode switch
            {
                "ja-JP" => $"[重要指示: 必ず以下の『要約する選択範囲』のテキストが書かれている実際の言語と『同一の言語』でのみ要約を出力してください。絶対に他の言語に翻訳しないでください。]\n\n[要約する選択範囲]\n{text}",
                "zh-Hans" => $"[重要指示：必须只使用以下“要总结的选区”文本的实际语言输出摘要。绝对不要翻译成其他语言。]\n\n[要总结的选区]\n{text}",
                "zh-Hant" => $"[重要指示：必須只使用以下「要總結的選取內容」文字的實際語言輸出摘要。絕對不要翻譯成其他語言。]\n\n[要總結的選取內容]\n{text}",
                "en-US" => $"[CRITICAL INSTRUCTION: You MUST output the summary in the EXACT SAME LANGUAGE as the 'Selection to Summarize' text below. Do NOT translate it under any circumstances.]\n\n[Selection to Summarize]\n{text}",
                _ => $"[중요 지침: 반드시 아래의 '요약할 선택 영역'의 텍스트가 작성된 실제 언어와 '동일한 언어'로만 요약 결과를 출력하십시오. 절대 다른 언어로 번역하지 마십시오.]\n\n[요약할 선택 영역]\n{text}"
            };

            return await ExecuteLlmAsync(systemPrompt, userContent, onChunk, cancellationToken);
        }

        public async Task<string> TranslateTextAsync(string text, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default)
        {
            var settings = _settingsService.CurrentSettings;
            string langCode = GetActiveLanguage();
            string srcLang = settings.LlmSourceLanguage ?? "Auto";
            string tgtLang = settings.LlmTargetLanguage ?? "Default";
            if (string.IsNullOrEmpty(tgtLang) || tgtLang.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                tgtLang = langCode switch
                {
                    "ko-KR" => "Korean",
                    "ja-JP" => "Japanese",
                    "zh-Hant" => "Chinese Traditional",
                    "zh-Hans" => "Chinese Simplified",
                    _ => "English"
                };
            }

            string srcLangDisplay = srcLang switch
            {
                "Korean" => "한국어 (Korean)",
                "English" => "영어 (English)",
                "Japanese" => "일본어 (Japanese)",
                "Chinese" => "중국어 (Chinese)",
                "Chinese Simplified" => "중국어 간체 (Simplified Chinese)",
                "Chinese Traditional" => "중국어 번체 (Traditional Chinese)",
                "French" => "프랑스어 (French)",
                "Spanish" => "스페인어 (Spanish)",
                "German" => "독일어 (German)",
                _ => "자동 감지 (Auto Detect)"
            };

            string tgtLangDisplay = tgtLang switch
            {
                "English" => "영어 (English)",
                "Japanese" => "일본어 (Japanese)",
                "Chinese" => "중국어 (Chinese)",
                "Chinese Simplified" => "중국어 간체 (Simplified Chinese)",
                "Chinese Traditional" => "중국어 번체 (Traditional Chinese)",
                "French" => "프랑스어 (French)",
                "Spanish" => "스페인어 (Spanish)",
                "German" => "독일어 (German)",
                _ => "한국어 (Korean)"
            };

            string systemPrompt = langCode switch
            {
                "ja-JP" => $"あなたはプロの翻訳家です。ユーザーが提供した選択範囲のみを翻訳します。入力テキストの言語（{srcLangDisplay}）を翻訳対象言語（{tgtLangDisplay}）に正確に翻訳してください。コードブロック、Markdown構文、URL、ファイルパス、変数名、関数名、コマンドなどはそのまま保持し、コメントや一般の文のみを翻訳します。挨拶、導入説明、解説、要約、『以下は翻訳結果です』といったメタテキスト、および不要なマークダウンのコードブロック包み（```）などは一切追加せず、純粋な翻訳結果のテキストのみを出力してください。",
                "zh-Hans" => $"你是专业翻译。只翻译用户提供的选区。请将输入文本（源语言：{srcLangDisplay}）准确、自然地翻译为目标语言（目标：{tgtLangDisplay}）。保留代码块、Markdown 语法、URL、文件路径、变量名、函数名和命令，只翻译注释与普通文本。不要添加问候、说明、摘要、开场语、元评论或额外的 Markdown 代码块包裹（```），除非原文本身包含这些内容。只直接输出纯翻译结果。",
                "zh-Hant" => $"你是專業翻譯。只翻譯使用者提供的選取內容。請將輸入文字（來源語言：{srcLangDisplay}）準確、自然地翻譯為目標語言（目標：{tgtLangDisplay}）。保留程式碼區塊、Markdown 語法、URL、檔案路徑、變數名稱、函式名稱和命令，只翻譯註解與一般文字。不要加入問候、說明、摘要、開場語、元評論或額外的 Markdown 程式碼區塊包裹（```），除非原文本身包含這些內容。只直接輸出純翻譯結果。",
                "en-US" => $"You are a professional translator. Translate only the provided text selection. Translate the input text (Source: {srcLangDisplay}) to the target language (Target: {tgtLangDisplay}). Preserve code blocks, Markdown syntax, URLs, file paths, variable names, function names, and commands intact, translating only comments and prose. Do not add any greetings, explanations, summaries, introductory phrases, meta-commentary, or markdown code block wrapper backticks (e.g. ```) unless the original text itself contained them. Output ONLY the raw translated text directly.",
                _ => $"당신은 전문 번역가입니다. 사용자가 제공한 선택 영역만 번역합니다. 입력 텍스트(원본 언어: {srcLangDisplay})를 대상 언어({tgtLangDisplay})로 정확하고 자연스럽게 번역하십시오. 코드 블록, 마크다운 문법, URL, 파일 경로, 변수명, 함수명, 명령어는 그대로 유지하고 주석과 일반 문장만 번역합니다. 인사말, 도입부 설명, 역주(해설), 요약, 혹은 '번역 결과:' 같은 불필요한 부가 문구나 메타 텍스트를 절대 출력하지 마십시오. 번역 결과를 마크다운 코드 블록(```)으로 감싸지 말고(원문에 백틱이 포함된 경우 제외), 오직 순수한 번역 결과 텍스트 자체만 즉시 출력하십시오."
            };

            string userContent = langCode switch
            {
                "ja-JP" => $"[翻訳する選択範囲]\n{text}",
                "zh-Hans" => $"[要翻译的选区]\n{text}",
                "zh-Hant" => $"[要翻譯的選取內容]\n{text}",
                "en-US" => $"[Selection to Translate]\n{text}",
                _ => $"[번역할 선택 영역]\n{text}"
            };

            return await ExecuteLlmAsync(systemPrompt, userContent, onChunk, cancellationToken);
        }

        public async Task<string> ImproveTextAsync(string text, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default)
        {
            string langCode = GetActiveLanguage();
            string systemPrompt = langCode switch
            {
                "ja-JP" => "あなたはドキュメント改善のスペシャリストです。提供されたテキストの可読性、Markdownの書式、LaTeXの数式（数式の文法や標準的な表現など）を正確に検証・改善し、入力テキストと同じ言語の洗練された表現に修正してください。絶対に他の言語に翻訳しないでください。入力テキストが英語（English）なら必ず英語のままで、日本語（Japanese）なら必ず日本語で、韓国語（Korean）なら必ず韓国語で改善・精査してください。挨拶、余計な説明や『修正しました』などの補足テキスト、コードブロックでの強制的なラッピング（```）を一切行わず、改善・修正されたドキュメントのテキストのみを直接出力してください。",
                "zh-Hans" => "你是文档改进专家。请检查并改进所提供文本的可读性、Markdown 格式或 LaTeX 数学公式，并使用与输入文本完全相同的语言进行自然、精炼的表达。绝对不要翻译成其他语言。如果输入是英语，就用英语润色；如果是简体中文或繁体中文，就使用相同中文书写形式润色；如果是日语或韩语，也使用原语言润色。不要包含问候、说明、修改记录、元评论，也不要用 Markdown 代码块包裹整段结果（除非原文就是代码块）。只直接输出改进后的文本正文。",
                "zh-Hant" => "你是文件改進專家。請檢查並改進所提供文字的可讀性、Markdown 格式或 LaTeX 數學公式，並使用與輸入文字完全相同的語言進行自然、精煉的表達。絕對不要翻譯成其他語言。如果輸入是英文，就用英文潤飾；如果是簡體中文或繁體中文，就使用相同中文書寫形式潤飾；如果是日文或韓文，也使用原語言潤飾。不要包含問候、說明、修改紀錄、元評論，也不要用 Markdown 程式碼區塊包裹整段結果（除非原文就是程式碼區塊）。只直接輸出改進後的文字正文。",
                "en-US" => "You are a document improvement specialist. Inspect and improve the readability, Markdown formatting, or LaTeX mathematical formulas of the provided text, and refine it beautifully in the exact same language as the input text. Do NOT translate it to any other language. If the input is in English, refine it in English. If it is in Japanese, refine it in Japanese. If it is in Korean, refine it in Korean. Do not include any greetings, explanations, conversational filler, introductory words, or wrap the response in markdown code blocks (e.g., ```). Output ONLY the refined text directly.",
                _ => "당신은 문서 및 수식 정제 전문가입니다. 제공된 텍스트의 가독성, 마크다운(Markdown) 형식, 또는 LaTeX 수학 공식을 표준 문법과 예쁜 형식에 맞게 개선하여 입력 텍스트와 동일한 언어로 가장 자연스럽고 깔끔하게 정제해 주십시오. 절대로 다른 언어로 번역하지 마십시오. 만약 입력 텍스트가 영어라면 반드시 영어 그대로 개선하고, 한국어라면 한국어 그대로 개선해야 하며, 일본어라면 일본어 그대로 개선해야 합니다. 즉, 정제된 결과물은 반드시 입력 텍스트의 원래 언어와 100% 동일한 언어여야 합니다. 인사말, 수정 내역 설명, '개선 완료된 결과입니다:'와 같은 부가 설명이나 메타 코멘트를 단 한 단어도 포함하지 마십시오. 백틱 기호(```)를 사용해 결과물 전체를 마크다운 코드 블록으로 래핑하지 마십시오(원래 원문이 코드 블록이었던 경우 제외). 오직 정제 및 개선된 결과물 본문만 순수하게 직접 출력하십시오."
            };

            string userContent = langCode switch
            {
                "ja-JP" => $"[重要指示: 必ず以下の『改善する選択範囲』のテキストが書かれている実際の言語と『同一の言語』でのみ結果を出力してください。絶対に他の言語に翻訳しないでください。]\n\n[改善する選択範囲]\n{text}",
                "zh-Hans" => $"[重要指示：必须只使用以下“要改进的选区”文本的实际语言输出结果。绝对不要翻译成其他语言。]\n\n[要改进的选区]\n{text}",
                "zh-Hant" => $"[重要指示：必須只使用以下「要改進的選取內容」文字的實際語言輸出結果。絕對不要翻譯成其他語言。]\n\n[要改進的選取內容]\n{text}",
                "en-US" => $"[CRITICAL INSTRUCTION: You MUST output the refined result in the EXACT SAME LANGUAGE as the 'Selection to Improve' text below. Do NOT translate it under any circumstances.]\n\n[Selection to Improve]\n{text}",
                _ => $"[중요 지침: 반드시 아래의 '개선할 선택 영역'의 텍스트가 작성된 실제 언어와 '동일한 언어'로만 정제된 결과물을 출력하십시오. 절대 다른 언어로 번역하지 마십시오.]\n\n[개선할 선택 영역]\n{text}"
            };

            return await ExecuteLlmAsync(systemPrompt, userContent, onChunk, cancellationToken);
        }

        public async Task<string> CustomPromptAsync(string prompt, string fileContext, string selectedText, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default)
        {
            string langCode = GetActiveLanguage();
            string systemPrompt = langCode switch
            {
                "ja-JP" => "あなたは正確な開発アシスタントです。提供された選択範囲とファイルのコンテキストを根拠に、ユーザーの指示に回答します。選択範囲にない事実を断定せず、必要に応じて不確実性を明記してください。必ず指示に対する回答のみを出力し、挨拶や前置き、補足説明は一切含めないでください。回答は日本語で出力します。",
                "zh-Hans" => "你是严谨的开发助手。请严格依据所提供的选区和文件上下文回答用户指令。不要断言上下文中没有的事实，必要时请明确不确定性。只输出对指令的直接回答，不要包含问候、开场语或元评论。回答请使用简体中文。",
                "zh-Hant" => "你是嚴謹的開發助手。請嚴格依據所提供的選取內容和檔案上下文回答使用者指令。不要斷言上下文中沒有的事實，必要時請明確不確定性。只輸出對指令的直接回答，不要包含問候、開場語或元評論。回答請使用繁體中文。",
                "en-US" => "You are an accurate developer assistant. Answer the user's instructions based strictly on the provided text selection and file context. Do not assume facts outside the provided context, and state any uncertainty clearly. Output ONLY the direct answer to the instruction — no greetings, no introductory phrases, no meta-commentary. Write your response in English.",
                _ => "당신은 정확한 개발 보조자입니다. 제공된 선택 영역과 파일 컨텍스트를 근거로 사용자의 지시사항에 답합니다. 제공된 맥락에 없는 사실을 단정하지 말고, 필요한 경우 불확실성을 명시합니다. 반드시 지시에 대한 답변만 출력하고, 인사말이나 전제 설명, 부가 해설 등은 일절 포함하지 마십시오. 답변은 한국어로 작성합니다."
            };

            var userContentBuilder = new StringBuilder();
            userContentBuilder.Append($"[사용자 지시사항]\n{prompt}");

            if (!string.IsNullOrEmpty(fileContext))
            {
                userContentBuilder.Append($"\n\n{fileContext}");
            }

            if (!string.IsNullOrEmpty(selectedText))
            {
                userContentBuilder.Append($"\n\n[선택 영역]\n{selectedText}");
            }

            string userContent = userContentBuilder.ToString();

            return await ExecuteLlmAsync(systemPrompt, userContent, onChunk, cancellationToken);
        }

        public async Task<string> RunAgentAsync(string instruction, string workspaceContext, string selectedText, string mode, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, bool isPlanningMode = false, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, bool hasEnabledSkills = false, bool hasEnabledMcp = false, Func<LlmTokenUsage, Task>? onUsage = null)
        {
            string langCode = GetActiveLanguage();
            string targetLanguage = _settingsService.CurrentSettings?.LlmTargetLanguage ?? "Default";
            if (string.IsNullOrEmpty(targetLanguage) || targetLanguage.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                targetLanguage = langCode switch
                {
                    "ko-KR" => "Korean",
                    "ja-JP" => "Japanese",
                    "zh-Hant" => "Chinese Traditional",
                    "zh-Hans" => "Chinese Simplified",
                    _ => "English"
                };
            }
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(langCode, isPlanningMode, targetLanguage, hasEnabledSkills, hasEnabledMcp);
            string userContent = AgentPromptBuilder.BuildUserContent(instruction, workspaceContext, selectedText, string.Empty, langCode);
            return await ExecuteLlmAsync(systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning, tools, onUsage);
        }

        public async Task<string> RunAgentAsync(EditorSettings settings, string instruction, string workspaceContext, string selectedText, string mode, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, bool isPlanningMode = false, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, bool hasEnabledSkills = false, bool hasEnabledMcp = false, Func<LlmTokenUsage, Task>? onUsage = null)
        {
            string langCode = GetActiveLanguage(settings);
            string targetLanguage = settings.LlmTargetLanguage ?? "Default";
            if (string.IsNullOrEmpty(targetLanguage) || targetLanguage.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                targetLanguage = langCode switch
                {
                    "ko-KR" => "Korean",
                    "ja-JP" => "Japanese",
                    "zh-Hant" => "Chinese Traditional",
                    "zh-Hans" => "Chinese Simplified",
                    _ => "English"
                };
            }
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(langCode, isPlanningMode, targetLanguage, hasEnabledSkills, hasEnabledMcp);
            string userContent = AgentPromptBuilder.BuildUserContent(instruction, workspaceContext, selectedText, string.Empty, langCode);
            return await ExecuteLlmAsync(settings, systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning, tools, onUsage);
        }

        public Task SaveApiKeyAsync(string provider, string apiKey)
        {
            try
            {
                string targetName = $"TxtAIEditor_LLM_{provider}";
                if (string.IsNullOrEmpty(apiKey))
                {
                    _credentialService.DeleteCredential(targetName);
                }
                else
                {
                    _credentialService.WriteCredential(targetName, "TxtAIEditor_user", apiKey);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed storing API Key securely: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public Task<string> GetApiKeyAsync(string provider)
        {
            try
            {
                string targetName = $"TxtAIEditor_LLM_{provider}";
                string? key = _credentialService.ReadCredential(targetName);
                return Task.FromResult(key ?? string.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed reading secure API Key: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }

        // ----------------------------------------------------
        // Private dynamic Provider Dispatcher
        // ----------------------------------------------------

        private async Task<string> ExecuteLlmAsync(string systemPrompt, string userContent, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null)
        {
            return await ExecuteLlmAsync(_settingsService.CurrentSettings, systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning, tools, onUsage);
        }

        private async Task<string> ExecuteLlmAsync(EditorSettings settings, string systemPrompt, string userContent, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string providerName = settings.LlmProvider;
            string apiKey = await GetApiKeyAsync(providerName);
            bool requiresApiKey = !providerName.Equals("LM Studio", StringComparison.OrdinalIgnoreCase) &&
                                    !providerName.Equals("LMStudio", StringComparison.OrdinalIgnoreCase) &&
                                    !providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase) &&
                                    !providerName.Equals("OpenAI OAuth", StringComparison.OrdinalIgnoreCase) &&
                                    !providerName.Equals("OpenAIOAuth", StringComparison.OrdinalIgnoreCase);

            string langCode = GetActiveLanguage();
            if (requiresApiKey && string.IsNullOrEmpty(apiKey))
            {
                return _localizationService.GetString("LlmErrorNoApiKeyOrToken", "에러: 해당 LLM API Key가 자격 증명 관리자에 등록되어 있지 않습니다. 설정을 열어 자격 증명을 먼저 저장해 주십시오.");
            }

            ILLMProvider provider = providerName.ToLower() switch
            {
                "gemini" => new GeminiProvider(_localizationService, settings.LlmAgentVerbose, settings.LlmThinkingLevel, providerName),
                "openai oauth" => new OpenAIProvider(_localizationService, isOAuth: true, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName),
                "openaioauth" => new OpenAIProvider(_localizationService, isOAuth: true, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName),
                "cerebras" => new CerebrasProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "openrouter" => new OpenRouterProvider(_localizationService, providerName),
                "lm studio" => new LMStudioProvider(_localizationService),
                "lmstudio" => new LMStudioProvider(_localizationService),
                "ollama" => new OllamaProvider(_localizationService, isCloud: false, providerName: providerName),
                "ollama cloud" => new OllamaProvider(_localizationService, isCloud: true, providerName: providerName),
                "ollamacloud" => new OllamaProvider(_localizationService, isCloud: true, providerName: providerName),
                "opencode go" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "opencodego" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "go" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "opencode zen" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "opencodezen" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "zen" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                _ => new OpenAIProvider(_localizationService, isOAuth: false, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName)
            };

            try
            {
                LlmTokenUsage? observedUsage = null;
                Func<LlmTokenUsage, Task> onProviderUsage = async usage =>
                {
                    var usageWithContext = usage.WithContext(providerName, settings.LlmModel, DateTimeOffset.Now);
                    observedUsage = usageWithContext;
                    if (onUsage != null)
                    {
                        await onUsage(usageWithContext);
                    }
                };

                if (onChunk != null)
                {
                    var fullResponse = new StringBuilder();
                    var reasoningResponse = new StringBuilder();
                    await provider.GenerateCompletionStreamAsync(
                        settings.LlmEndpoint,
                        apiKey,
                        settings.LlmModel,
                        systemPrompt,
                        userContent,
                        async chunk =>
                        {
                            fullResponse.Append(chunk);
                            await onChunk(chunk);
                        },
                        cancellationToken,
                        attachments,
                        async reasoningChunk =>
                        {
                            reasoningResponse.Append(reasoningChunk);
                            if (onReasoning != null) await onReasoning(reasoningChunk);
                        },
                        tools,
                        onProviderUsage
                    );
                    if (observedUsage != null)
                    {
                        RecordTokenUsage(observedUsage);
                    }
                    return fullResponse.Length > 0 ? fullResponse.ToString() : reasoningResponse.ToString();
                }
                else
                {
                    string result = await provider.GenerateCompletionAsync(
                        settings.LlmEndpoint,
                        apiKey,
                        settings.LlmModel,
                        systemPrompt,
                        userContent,
                        cancellationToken,
                        attachments,
                        tools,
                        onProviderUsage
                    );
                    if (observedUsage != null)
                    {
                        RecordTokenUsage(observedUsage);
                    }
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ResponseTruncatedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string errorPrefix = _localizationService.GetString("LlmErrorCommunicationPrefix", "AI 통신 오류가 발생했습니다: ");
                return $"{errorPrefix}{ex.Message}";
            }
        }

        private void RecordTokenUsage(LlmTokenUsage usage)
        {
            if (!usage.HasAny)
            {
                return;
            }

            LlmTokenUsageStats snapshot;
            lock (_tokenUsageStatsLock)
            {
                _lastTokenUsage = usage;
                string provider = string.IsNullOrWhiteSpace(usage.Provider) ? "Unknown" : usage.Provider.Trim();
                string model = string.IsNullOrWhiteSpace(usage.Model) ? "Unknown" : usage.Model.Trim();
                string bucketKey = $"{provider}\u001f{model}";

                if (!_tokenUsageBuckets.TryGetValue(bucketKey, out var bucket))
                {
                    bucket = new MutableTokenUsageBucket
                    {
                        Provider = provider,
                        Model = model
                    };
                    _tokenUsageBuckets[bucketKey] = bucket;
                }

                bucket.RequestCount++;
                bucket.PromptTokens += usage.PromptTokens ?? 0;
                bucket.CompletionTokens += usage.CompletionTokens ?? 0;
                bucket.TotalTokens += usage.TotalTokens ?? 0;
                bucket.CachedTokens += usage.CachedTokens ?? 0;

                DateTimeOffset observedAt = usage.ObservedAt ?? DateTimeOffset.Now;
                AddPeriodUsage(_tokenUsageDayBuckets, observedAt.ToLocalTime().ToString("yyyy-MM-dd"), usage);
                AddPeriodUsage(_tokenUsageMonthBuckets, observedAt.ToLocalTime().ToString("yyyy-MM"), usage);

                _tokenUsageStats = CreateTokenUsageStatsSnapshot();
                snapshot = _tokenUsageStats;
            }

            SaveTokenUsageStats(snapshot);
        }

        private LlmTokenUsageStats CreateTokenUsageStatsSnapshot()
        {
            var buckets = _tokenUsageBuckets.Values
                .OrderByDescending(item => item.CachedTokens)
                .ThenByDescending(item => item.TotalTokens)
                .Select(item => item.ToSnapshot())
                .ToArray();
            var dayBuckets = _tokenUsageDayBuckets.Values
                .OrderBy(item => item.Period)
                .Select(item => item.ToSnapshot())
                .ToArray();
            var monthBuckets = _tokenUsageMonthBuckets.Values
                .OrderBy(item => item.Period)
                .Select(item => item.ToSnapshot())
                .ToArray();

            return new LlmTokenUsageStats
            {
                RequestCount = buckets.Sum(item => item.RequestCount),
                PromptTokens = buckets.Sum(item => item.PromptTokens),
                CompletionTokens = buckets.Sum(item => item.CompletionTokens),
                TotalTokens = buckets.Sum(item => item.TotalTokens),
                CachedTokens = buckets.Sum(item => item.CachedTokens),
                LastUsage = _lastTokenUsage,
                ByProviderModel = buckets,
                ByDay = dayBuckets,
                ByMonth = monthBuckets
            };
        }

        private static void AddPeriodUsage(Dictionary<string, MutableTokenUsagePeriodBucket> buckets, string period, LlmTokenUsage usage)
        {
            if (!buckets.TryGetValue(period, out var bucket))
            {
                bucket = new MutableTokenUsagePeriodBucket
                {
                    Period = period
                };
                buckets[period] = bucket;
            }

            bucket.RequestCount++;
            bucket.PromptTokens += usage.PromptTokens ?? 0;
            bucket.CompletionTokens += usage.CompletionTokens ?? 0;
            bucket.TotalTokens += usage.TotalTokens ?? 0;
            bucket.CachedTokens += usage.CachedTokens ?? 0;
        }

        private void LoadTokenUsageStats()
        {
            try
            {
                string path = GetTokenUsageStatsPath();
                if (!File.Exists(path))
                {
                    return;
                }

                string json = File.ReadAllText(path);
                var persisted = JsonSerializer.Deserialize<PersistedTokenUsageStats>(json);
                if (persisted == null)
                {
                    return;
                }

                lock (_tokenUsageStatsLock)
                {
                    _tokenUsageBuckets.Clear();
                    _tokenUsageDayBuckets.Clear();
                    _tokenUsageMonthBuckets.Clear();

                    foreach (var bucket in persisted.ByProviderModel ?? new List<LlmTokenUsageBucket>())
                    {
                        string key = $"{bucket.Provider}\u001f{bucket.Model}";
                        _tokenUsageBuckets[key] = new MutableTokenUsageBucket
                        {
                            Provider = bucket.Provider,
                            Model = bucket.Model,
                            RequestCount = bucket.RequestCount,
                            PromptTokens = bucket.PromptTokens,
                            CompletionTokens = bucket.CompletionTokens,
                            TotalTokens = bucket.TotalTokens,
                            CachedTokens = bucket.CachedTokens
                        };
                    }

                    foreach (var bucket in persisted.ByDay ?? new List<LlmTokenUsagePeriodBucket>())
                    {
                        _tokenUsageDayBuckets[bucket.Period] = new MutableTokenUsagePeriodBucket
                        {
                            Period = bucket.Period,
                            RequestCount = bucket.RequestCount,
                            PromptTokens = bucket.PromptTokens,
                            CompletionTokens = bucket.CompletionTokens,
                            TotalTokens = bucket.TotalTokens,
                            CachedTokens = bucket.CachedTokens
                        };
                    }

                    foreach (var bucket in persisted.ByMonth ?? new List<LlmTokenUsagePeriodBucket>())
                    {
                        _tokenUsageMonthBuckets[bucket.Period] = new MutableTokenUsagePeriodBucket
                        {
                            Period = bucket.Period,
                            RequestCount = bucket.RequestCount,
                            PromptTokens = bucket.PromptTokens,
                            CompletionTokens = bucket.CompletionTokens,
                            TotalTokens = bucket.TotalTokens,
                            CachedTokens = bucket.CachedTokens
                        };
                    }

                    _lastTokenUsage = persisted.LastUsage;
                    _tokenUsageStats = CreateTokenUsageStatsSnapshot();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed loading LLM token usage stats: {ex.Message}");
            }
        }

        private void SaveTokenUsageStats(LlmTokenUsageStats stats)
        {
            try
            {
                string path = GetTokenUsageStatsPath();
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var persisted = new PersistedTokenUsageStats
                {
                    LastUsage = stats.LastUsage,
                    ByProviderModel = stats.ByProviderModel.ToList(),
                    ByDay = stats.ByDay.ToList(),
                    ByMonth = stats.ByMonth.ToList()
                };
                string json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed saving LLM token usage stats: {ex.Message}");
            }
        }

        private void DeleteTokenUsageStatsFile()
        {
            try
            {
                string path = GetTokenUsageStatsPath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed deleting LLM token usage stats: {ex.Message}");
            }
        }

        private static string GetTokenUsageStatsPath()
        {
            return Path.Combine(SettingsBackupService.SettingsDirectoryPath, "llm-token-usage-stats.json");
        }

        private sealed class MutableTokenUsageBucket
        {
            public string Provider { get; init; } = string.Empty;
            public string Model { get; init; } = string.Empty;
            public int RequestCount { get; set; }
            public long PromptTokens { get; set; }
            public long CompletionTokens { get; set; }
            public long TotalTokens { get; set; }
            public long CachedTokens { get; set; }

            public LlmTokenUsageBucket ToSnapshot()
            {
                return new LlmTokenUsageBucket
                {
                    Provider = Provider,
                    Model = Model,
                    RequestCount = RequestCount,
                    PromptTokens = PromptTokens,
                    CompletionTokens = CompletionTokens,
                    TotalTokens = TotalTokens,
                    CachedTokens = CachedTokens
                };
            }
        }

        private sealed class MutableTokenUsagePeriodBucket
        {
            public string Period { get; init; } = string.Empty;
            public int RequestCount { get; set; }
            public long PromptTokens { get; set; }
            public long CompletionTokens { get; set; }
            public long TotalTokens { get; set; }
            public long CachedTokens { get; set; }

            public LlmTokenUsagePeriodBucket ToSnapshot()
            {
                return new LlmTokenUsagePeriodBucket
                {
                    Period = Period,
                    RequestCount = RequestCount,
                    PromptTokens = PromptTokens,
                    CompletionTokens = CompletionTokens,
                    TotalTokens = TotalTokens,
                    CachedTokens = CachedTokens
                };
            }
        }

        private sealed class PersistedTokenUsageStats
        {
            public LlmTokenUsage? LastUsage { get; set; }
            public List<LlmTokenUsageBucket> ByProviderModel { get; set; } = new();
            public List<LlmTokenUsagePeriodBucket> ByDay { get; set; } = new();
            public List<LlmTokenUsagePeriodBucket> ByMonth { get; set; } = new();
        }

        // ----------------------------------------------------
        // Exa Search Implementation
        // ----------------------------------------------------
        private static readonly HttpClient _exaHttpClient = new HttpClient();
        private const string DefaultExaMcpEndpoint = "https://mcp.exa.ai/mcp";
        private const int NoKeyFetchMaxCharacters = 6000;

        private static bool IsExaMcpEndpoint(string endpoint)
        {
            return endpoint.Contains("/mcp", StringComparison.OrdinalIgnoreCase) ||
                   endpoint.Contains("/sse", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> SearchExaAsync(string query, int numResults, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "Exa search failed: query is empty.";
            }

            string apiKey = await GetApiKeyAsync("Exa");
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("EXA_API_KEY") ?? string.Empty;
            }

            string endpoint = _settingsService?.CurrentSettings?.ExaEndpoint ?? DefaultExaMcpEndpoint;
            bool isMcpEndpoint = IsExaMcpEndpoint(endpoint);
            bool triedDefaultMcpEndpoint = isMcpEndpoint &&
                string.Equals(endpoint.TrimEnd('/'), DefaultExaMcpEndpoint, StringComparison.OrdinalIgnoreCase);

            // If it is configured as an MCP / SSE URL (e.g. contains '/mcp' or '/sse'), use the MCP SSE transport client.
            if (isMcpEndpoint)
            {
                try
                {
                    int mcpResultsCount = numResults <= 0 ? 5 : Math.Min(numResults, 10);
                    var arguments = new
                    {
                        query = query,
                        numResults = mcpResultsCount,
                        highlights = true
                    };
                    return await CallMcpSseToolAsync(endpoint, apiKey, "web_search_exa", arguments, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Fallback to direct REST search if MCP fails
                    System.Diagnostics.Debug.WriteLine($"Exa MCP failed, falling back to direct search: {ex.Message}");
                }
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                if (!triedDefaultMcpEndpoint)
                {
                    try
                    {
                        int mcpResultsCount = numResults <= 0 ? 5 : Math.Min(numResults, 10);
                        var arguments = new
                        {
                            query = query,
                            numResults = mcpResultsCount,
                            highlights = true
                        };
                        return await CallMcpSseToolAsync(DefaultExaMcpEndpoint, apiKey, "web_search_exa", arguments, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Exa no-key MCP fallback failed, falling back to DuckDuckGo: {ex.Message}");
                    }
                }

                return await SearchWebWithoutApiKeyAsync(query, numResults, cancellationToken);
            }

            int resultsCount = numResults <= 0 ? 5 : Math.Min(numResults, 10);
            
            // If the endpoint is just a domain or base URL without path, default to /search
            string requestUrl = endpoint;
            if (isMcpEndpoint)
            {
                // If we fell back here because MCP failed, force standard API URL
                requestUrl = "https://api.exa.ai/search";
            }
            else if (!requestUrl.Contains("/search") && !requestUrl.Contains("/findSimilar"))
            {
                requestUrl = requestUrl.TrimEnd('/') + "/search";
                if (!requestUrl.StartsWith("http://") && !requestUrl.StartsWith("https://"))
                {
                    requestUrl = "https://api.exa.ai/search";
                }
            }

            var payload = new
            {
                query = query,
                useAutoprompt = true,
                numResults = resultsCount,
                text = new { maxCharacters = 1000 },
                highlights = true
            };

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
                {
                    request.Headers.Add("x-api-key", apiKey);
                    string jsonPayload = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    using (var response = await _exaHttpClient.SendAsync(request, cancellationToken))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            return $"Exa search API failed ({response.StatusCode}): {responseBody}";
                        }

                        using (var doc = JsonDocument.Parse(responseBody))
                        {
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                            {
                                return "Exa search returned no results format.";
                            }

                            var sb = new StringBuilder();
                            int index = 1;
                            foreach (var item in results.EnumerateArray())
                            {
                                string title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "No Title" : "No Title";
                                string url = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                                string textContent = item.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                                
                                sb.AppendLine($"[{index}] Title: {title}");
                                sb.AppendLine($"URL: {url}");
                                
                                if (item.TryGetProperty("highlights", out var highlightsProp) && highlightsProp.ValueKind == JsonValueKind.Array && highlightsProp.GetArrayLength() > 0)
                                {
                                    sb.AppendLine("Highlights:");
                                    foreach (var highlight in highlightsProp.EnumerateArray())
                                    {
                                        sb.AppendLine($"- {highlight.GetString()}");
                                    }
                                }
                                else if (!string.IsNullOrEmpty(textContent))
                                {
                                    string preview = textContent.Length > 300 ? textContent.Substring(0, 300) + "..." : textContent;
                                    sb.AppendLine($"Snippet: {preview}");
                                }
                                sb.AppendLine();
                                index++;
                            }

                            return sb.ToString().TrimEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Exa search exception occurred: {ex.Message}";
            }
        }

        private async Task<string> SearchWebWithoutApiKeyAsync(string query, int numResults, CancellationToken cancellationToken)
        {
            int resultsCount = numResults <= 0 ? 5 : Math.Min(numResults, 10);
            string requestUrl = "https://duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
                {
                    AddNoKeyWebHeaders(request);

                    using (var response = await _exaHttpClient.SendAsync(request, cancellationToken))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            return $"No-key web search failed ({response.StatusCode}): {LimitText(StripHtmlToText(responseBody), 1000)}";
                        }

                        var matches = Regex.Matches(
                            responseBody,
                            "<a[^>]*class=[\"'][^\"']*result__a[^\"']*[\"'][^>]*href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<title>.*?)</a>",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline);

                        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var sb = new StringBuilder();
                        int index = 1;

                        foreach (Match match in matches)
                        {
                            if (index > resultsCount)
                            {
                                break;
                            }

                            string title = StripHtmlToText(match.Groups["title"].Value);
                            string url = DecodeDuckDuckGoHref(match.Groups["href"].Value);
                            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
                            {
                                continue;
                            }

                            sb.AppendLine($"[{index}] Title: {title}");
                            sb.AppendLine($"URL: {url}");
                            sb.AppendLine("Snippet: No-key fallback result from DuckDuckGo HTML search.");
                            sb.AppendLine();
                            index++;
                        }

                        if (sb.Length == 0)
                        {
                            string pageText = LimitText(StripHtmlToText(responseBody), 1000);
                            return string.IsNullOrWhiteSpace(pageText)
                                ? "No-key web search returned no results."
                                : $"No-key web search returned no parsed results. Raw page text:\n{pageText}";
                        }

                        return sb.ToString().TrimEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                return $"No-key web search exception occurred: {ex.Message}";
            }
        }

        private async Task<string> CallMcpSseToolAsync(
            string endpointUrl,
            string apiKey,
            string toolName,
            object arguments,
            CancellationToken cancellationToken)
        {
            if (endpointUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("MCP endpoint must use HTTPS.");
            }

            if (!endpointUrl.Contains("/sse", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return await CallMcpHttpToolAsync(endpointUrl, apiKey, toolName, arguments, cancellationToken);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MCP HTTP transport failed, falling back to SSE transport: {ex.Message}");
                }
            }

            using (var sseClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                using (var sseRequest = new HttpRequestMessage(HttpMethod.Get, endpointUrl))
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        sseRequest.Headers.Add("x-api-key", apiKey);
                    }
                    sseRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using (var sseResponse = await sseClient.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if (!sseResponse.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"MCP SSE connection failed: {sseResponse.StatusCode}");
                        }

                        using (System.IO.Stream stream = await sseResponse.Content.ReadAsStreamAsync(cancellationToken))
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            string? postEndpoint = null;
                            string? currentEvent = null;
                            var currentData = new StringBuilder();

                            // Step 1: Read SSE stream until we get the post endpoint
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string? line = await reader.ReadLineAsync();
                                if (line == null) break;

                                if (string.IsNullOrWhiteSpace(line))
                                {
                                    if (currentEvent == "endpoint" && currentData.Length > 0)
                                    {
                                        postEndpoint = ParseEndpointUri(currentData.ToString().Trim());
                                        break;
                                    }
                                    currentEvent = null;
                                    currentData.Clear();
                                    continue;
                                }

                                if (line.StartsWith("event:"))
                                {
                                    currentEvent = line.Substring(6).Trim();
                                }
                                else if (line.StartsWith("data:"))
                                {
                                    if (currentData.Length > 0)
                                    {
                                        currentData.Append("\n");
                                    }
                                    currentData.Append(line.Substring(5).Trim());
                                }
                            }

                            // Edge case fallback if connection ends before a blank line
                            if (postEndpoint == null && currentEvent == "endpoint" && currentData.Length > 0)
                            {
                                postEndpoint = ParseEndpointUri(currentData.ToString().Trim());
                            }

                            if (string.IsNullOrEmpty(postEndpoint))
                            {
                                throw new InvalidOperationException("MCP server did not provide a post endpoint.");
                            }

                            // Resolve absolute post URL
                            string postUrl = postEndpoint;
                            if (!postUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                                !postUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                var baseUri = new Uri(endpointUrl);
                                var resolvedUri = new Uri(baseUri, postUrl);
                                postUrl = resolvedUri.ToString();
                            }

                            using (var postClient = new HttpClient())
                            {
                                // Step 2: Send initialize request
                                string initId = "init_" + Guid.NewGuid().ToString("N");
                                var initPayload = new
                                {
                                    jsonrpc = "2.0",
                                    method = "initialize",
                                    @params = new
                                    {
                                        protocolVersion = "2024-11-05",
                                        capabilities = new { },
                                        clientInfo = new
                                        {
                                            name = "TxtAIEditor",
                                            version = "1.0.0"
                                        }
                                    },
                                    id = initId
                                };

                                string initJson = JsonSerializer.Serialize(initPayload);
                                using (var initRequest = new HttpRequestMessage(HttpMethod.Post, postUrl))
                                {
                                    if (!string.IsNullOrEmpty(apiKey))
                                    {
                                        initRequest.Headers.Add("x-api-key", apiKey);
                                    }
                                    initRequest.Content = new StringContent(initJson, Encoding.UTF8, "application/json");

                                    var initResponse = await postClient.SendAsync(initRequest, cancellationToken);
                                    if (!initResponse.IsSuccessStatusCode)
                                    {
                                        string errBody = await initResponse.Content.ReadAsStringAsync(cancellationToken);
                                        throw new HttpRequestException($"MCP initialization POST failed: {initResponse.StatusCode}\n{errBody}");
                                    }
                                }

                                // Step 3: Wait for initialize response on SSE stream
                                currentEvent = null;
                                currentData.Clear();
                                bool initialized = false;

                                while (!initialized)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    string? line = await reader.ReadLineAsync();
                                    if (line == null) break;

                                    if (string.IsNullOrWhiteSpace(line))
                                    {
                                        if (currentEvent == "message" && currentData.Length > 0)
                                        {
                                            string dataValue = currentData.ToString().Trim();
                                            using (var doc = JsonDocument.Parse(dataValue))
                                            {
                                                var root = doc.RootElement;
                                                if (root.TryGetProperty("id", out var idProp) && idProp.GetString() == initId)
                                                {
                                                    if (root.TryGetProperty("error", out var errorProp))
                                                    {
                                                        throw new InvalidOperationException($"MCP initialization failed: {errorProp.GetRawText()}");
                                                    }
                                                    initialized = true;
                                                }
                                            }
                                        }
                                        currentEvent = null;
                                        currentData.Clear();
                                        continue;
                                    }

                                    if (line.StartsWith("event:"))
                                    {
                                        currentEvent = line.Substring(6).Trim();
                                    }
                                    else if (line.StartsWith("data:"))
                                    {
                                        if (currentData.Length > 0)
                                        {
                                            currentData.Append("\n");
                                        }
                                        currentData.Append(line.Substring(5).Trim());
                                    }
                                }

                                if (!initialized)
                                {
                                    throw new InvalidOperationException("MCP connection closed before initialization response was received.");
                                }

                                // Step 4: Send notifications/initialized notification
                                var initializedNotification = new
                                {
                                    jsonrpc = "2.0",
                                    method = "notifications/initialized"
                                };
                                string notificationJson = JsonSerializer.Serialize(initializedNotification);
                                using (var notificationRequest = new HttpRequestMessage(HttpMethod.Post, postUrl))
                                {
                                    if (!string.IsNullOrEmpty(apiKey))
                                    {
                                        notificationRequest.Headers.Add("x-api-key", apiKey);
                                    }
                                    notificationRequest.Content = new StringContent(notificationJson, Encoding.UTF8, "application/json");
                                    await postClient.SendAsync(notificationRequest, cancellationToken);
                                }

                                // Step 5: Send tools/call request
                                string callId = "call_" + Guid.NewGuid().ToString("N");
                                var callPayload = new
                                {
                                    jsonrpc = "2.0",
                                    method = "tools/call",
                                    @params = new
                                    {
                                        name = toolName,
                                        arguments = arguments
                                    },
                                    id = callId
                                };
                                string callJson = JsonSerializer.Serialize(callPayload);
                                using (var callRequest = new HttpRequestMessage(HttpMethod.Post, postUrl))
                                {
                                    if (!string.IsNullOrEmpty(apiKey))
                                    {
                                        callRequest.Headers.Add("x-api-key", apiKey);
                                    }
                                    callRequest.Content = new StringContent(callJson, Encoding.UTF8, "application/json");

                                    var callResponse = await postClient.SendAsync(callRequest, cancellationToken);
                                    if (!callResponse.IsSuccessStatusCode)
                                    {
                                        string errBody = await callResponse.Content.ReadAsStringAsync(cancellationToken);
                                        throw new HttpRequestException($"MCP tool call POST failed: {callResponse.StatusCode}\n{errBody}");
                                    }
                                }

                                // Step 6: Wait for tool response on SSE stream
                                currentEvent = null;
                                currentData.Clear();
                                while (true)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    string? line = await reader.ReadLineAsync();
                                    if (line == null) break;

                                    if (string.IsNullOrWhiteSpace(line))
                                    {
                                        if (currentEvent == "message" && currentData.Length > 0)
                                        {
                                            string dataValue = currentData.ToString().Trim();
                                            using (var doc = JsonDocument.Parse(dataValue))
                                            {
                                                var root = doc.RootElement;
                                                if (root.TryGetProperty("id", out var idProp) && idProp.GetString() == callId)
                                                {
                                                    if (root.TryGetProperty("error", out var errorProp))
                                                    {
                                                        throw new InvalidOperationException($"MCP tool execution failed: {errorProp.GetRawText()}");
                                                    }

                                                    if (root.TryGetProperty("result", out var resultProp))
                                                    {
                                                        return FormatMcpSearchResult(resultProp);
                                                    }
                                                }
                                            }
                                        }
                                        currentEvent = null;
                                        currentData.Clear();
                                        continue;
                                    }

                                    if (line.StartsWith("event:"))
                                    {
                                        currentEvent = line.Substring(6).Trim();
                                    }
                                    else if (line.StartsWith("data:"))
                                    {
                                        if (currentData.Length > 0)
                                        {
                                            currentData.Append("\n");
                                        }
                                        currentData.Append(line.Substring(5).Trim());
                                    }
                                }
                            }
                        }
                    }
                }
            }

            throw new InvalidOperationException("MCP connection closed without response.");
        }

        private async Task<string> CallMcpHttpToolAsync(
            string endpointUrl,
            string apiKey,
            string toolName,
            object arguments,
            CancellationToken cancellationToken)
        {
            if (!endpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("MCP endpoint must use HTTPS.");
            }

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                string initId = "init_" + Guid.NewGuid().ToString("N");
                var initPayload = new
                {
                    jsonrpc = "2.0",
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { },
                        clientInfo = new
                        {
                            name = "TxtAIEditor",
                            version = "1.0.0"
                        }
                    },
                    id = initId
                };

                using (var initResponse = await SendMcpHttpRequestAsync(client, endpointUrl, apiKey, null, initPayload, cancellationToken))
                {
                    string initBody = await initResponse.Content.ReadAsStringAsync(cancellationToken);
                    if (!initResponse.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"MCP HTTP initialize failed: {initResponse.StatusCode}\n{initBody}");
                    }

                    JsonElement initRoot = ParseMcpHttpResponse(initBody, initId);
                    if (initRoot.TryGetProperty("error", out var initErrorProp))
                    {
                        throw new InvalidOperationException($"MCP HTTP initialize failed: {initErrorProp.GetRawText()}");
                    }

                    string sessionId = GetMcpSessionId(initResponse);
                    if (string.IsNullOrWhiteSpace(sessionId))
                    {
                        throw new InvalidOperationException("MCP HTTP server did not return Mcp-Session-Id.");
                    }

                    var initializedNotification = new
                    {
                        jsonrpc = "2.0",
                        method = "notifications/initialized"
                    };
                    using (var notificationResponse = await SendMcpHttpRequestAsync(client, endpointUrl, apiKey, sessionId, initializedNotification, cancellationToken))
                    {
                        if (!notificationResponse.IsSuccessStatusCode)
                        {
                            string errBody = await notificationResponse.Content.ReadAsStringAsync(cancellationToken);
                            throw new HttpRequestException($"MCP HTTP initialized notification failed: {notificationResponse.StatusCode}\n{errBody}");
                        }
                    }

                    string callId = "call_" + Guid.NewGuid().ToString("N");
                    var callPayload = new
                    {
                        jsonrpc = "2.0",
                        method = "tools/call",
                        @params = new
                        {
                            name = toolName,
                            arguments = arguments
                        },
                        id = callId
                    };

                    using (var callResponse = await SendMcpHttpRequestAsync(client, endpointUrl, apiKey, sessionId, callPayload, cancellationToken))
                    {
                        string callBody = await callResponse.Content.ReadAsStringAsync(cancellationToken);
                        if (!callResponse.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"MCP HTTP tool call failed: {callResponse.StatusCode}\n{callBody}");
                        }

                        JsonElement callRoot = ParseMcpHttpResponse(callBody, callId);
                        if (callRoot.TryGetProperty("error", out var callErrorProp))
                        {
                            throw new InvalidOperationException($"MCP HTTP tool execution failed: {callErrorProp.GetRawText()}");
                        }

                        if (callRoot.TryGetProperty("result", out var resultProp))
                        {
                            return FormatMcpSearchResult(resultProp);
                        }

                        return callRoot.GetRawText();
                    }
                }
            }
        }

        private static async Task<HttpResponseMessage> SendMcpHttpRequestAsync(
            HttpClient client,
            string endpointUrl,
            string apiKey,
            string? sessionId,
            object payload,
            CancellationToken cancellationToken)
        {
            string jsonPayload = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Add("x-api-key", apiKey);
            }
            if (!string.IsNullOrEmpty(sessionId))
            {
                request.Headers.Add("Mcp-Session-Id", sessionId);
            }
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await client.SendAsync(request, cancellationToken);
        }

        private static string GetMcpSessionId(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                foreach (string value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }

            return string.Empty;
        }

        private static JsonElement ParseMcpHttpResponse(string responseBody, string expectedId)
        {
            string json = ExtractMcpSseJson(responseBody, expectedId);
            using (var doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        if (JsonRpcIdMatches(item, expectedId))
                        {
                            return item.Clone();
                        }
                    }

                    if (root.GetArrayLength() > 0)
                    {
                        return root[0].Clone();
                    }
                }

                return root.Clone();
            }
        }

        private static string ExtractMcpSseJson(string responseBody, string expectedId)
        {
            string trimmed = responseBody.Trim();
            if (!trimmed.StartsWith("event:", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            string? currentEvent = null;
            var currentData = new StringBuilder();
            string? firstData = null;

            using (var reader = new System.IO.StringReader(responseBody))
            {
                while (true)
                {
                    string? line = reader.ReadLine();
                    if (line == null || string.IsNullOrWhiteSpace(line))
                    {
                        string? dataValue = FlushMcpSseData(currentEvent, currentData);
                        if (!string.IsNullOrWhiteSpace(dataValue))
                        {
                            firstData ??= dataValue;
                            try
                            {
                                using (var doc = JsonDocument.Parse(dataValue))
                                {
                                    if (JsonRpcIdMatches(doc.RootElement, expectedId))
                                    {
                                        return dataValue;
                                    }
                                }
                            }
                            catch
                            {
                                // Keep looking for a JSON-RPC message.
                            }
                        }

                        currentEvent = null;
                        currentData.Clear();
                        if (line == null)
                        {
                            break;
                        }
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEvent = line.Substring(6).Trim();
                    }
                    else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currentData.Length > 0)
                        {
                            currentData.Append("\n");
                        }
                        currentData.Append(line.Substring(5).Trim());
                    }
                }
            }

            return firstData ?? trimmed;
        }

        private static string? FlushMcpSseData(string? currentEvent, StringBuilder currentData)
        {
            if (currentData.Length == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(currentEvent) &&
                !currentEvent.Equals("message", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return currentData.ToString().Trim();
        }

        private static bool JsonRpcIdMatches(JsonElement root, string expectedId)
        {
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("id", out var idProp) &&
                idProp.ValueKind == JsonValueKind.String &&
                string.Equals(idProp.GetString(), expectedId, StringComparison.Ordinal);
        }

        private static string ParseEndpointUri(string data)
        {
            data = data.Trim();
            if (data.StartsWith("{") && data.EndsWith("}"))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(data))
                    {
                        if (doc.RootElement.TryGetProperty("uri", out var uriProp))
                        {
                            return uriProp.GetString() ?? data;
                        }
                    }
                }
                catch
                {
                    // Fallback to raw string
                }
            }
            return data;
        }

        private string FormatMcpSearchResult(JsonElement resultProp)
        {
            if (!resultProp.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.Array)
            {
                return resultProp.GetRawText();
            }

            var sb = new StringBuilder();
            foreach (var item in contentProp.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "text")
                {
                    if (item.TryGetProperty("text", out var textProp))
                    {
                        sb.AppendLine(textProp.GetString());
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

        public async Task<string> FetchExaAsync(string[] urls, CancellationToken cancellationToken = default)
        {
            if (urls == null || urls.Length == 0)
            {
                return "Exa fetch failed: urls list is empty.";
            }

            // 1. Try custom WebFetchService first
            try
            {
                System.Diagnostics.Debug.WriteLine("Attempting custom WebFetchService...");
                var fetchService = new WebFetchService();
                var sb = new StringBuilder();
                int index = 1;
                bool allSuccessful = true;

                foreach (string rawUrl in urls)
                {
                    string url = (rawUrl ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    try
                    {
                        string markdown = await fetchService.FetchUrlAsMarkdownAsync(url, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(markdown))
                        {
                            sb.AppendLine($"[{index}] URL: {url}");
                            sb.AppendLine("Content:");
                            sb.AppendLine(markdown);
                            sb.AppendLine();
                        }
                        else
                        {
                            // If returned markdown is empty, treat as failure to fallback to Exa
                            allSuccessful = false;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Custom WebFetchService failed for {url}: {ex.Message}");
                        allSuccessful = false;
                        break;
                    }
                    index++;
                }

                if (allSuccessful && sb.Length > 0)
                {
                    System.Diagnostics.Debug.WriteLine("Custom WebFetchService succeeded for all URLs.");
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during custom WebFetchService execution: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("Falling back to Exa content fetch...");

            // 2. Fallback to Exa content fetch
            string apiKey = await GetApiKeyAsync("Exa");
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("EXA_API_KEY") ?? string.Empty;
            }

            string endpoint = _settingsService?.CurrentSettings?.ExaEndpoint ?? "https://mcp.exa.ai/mcp";
            bool isMcpEndpoint = IsExaMcpEndpoint(endpoint);

            // If it is configured as an MCP / SSE URL (e.g. contains '/mcp' or '/sse'), use the MCP SSE transport client.
            if (isMcpEndpoint)
            {
                try
                {
                    var arguments = new
                    {
                        urls = urls
                    };
                    return await CallMcpSseToolAsync(endpoint, apiKey, "web_fetch_exa", arguments, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Fallback to direct REST contents fetch if MCP fails
                    System.Diagnostics.Debug.WriteLine($"Exa fetch MCP failed, falling back to direct API: {ex.Message}");
                }
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                return await FetchUrlsWithoutApiKeyAsync(urls, cancellationToken);
            }

            // Fallback direct REST API call: POST https://api.exa.ai/contents
            string requestUrl = endpoint;
            if (isMcpEndpoint)
            {
                requestUrl = "https://api.exa.ai/contents";
            }
            else if (!requestUrl.Contains("/contents"))
            {
                requestUrl = requestUrl.TrimEnd('/') + "/contents";
                if (!requestUrl.StartsWith("http://") && !requestUrl.StartsWith("https://"))
                {
                    requestUrl = "https://api.exa.ai/contents";
                }
            }

            var payload = new
            {
                urls = urls,
                text = true
            };

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
                {
                    request.Headers.Add("x-api-key", apiKey);
                    string jsonPayload = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    using (var response = await _exaHttpClient.SendAsync(request, cancellationToken))
                    {
                        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            return $"Exa fetch API failed ({response.StatusCode}): {responseBody}";
                        }

                        using (var doc = JsonDocument.Parse(responseBody))
                        {
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                            {
                                return "Exa fetch returned no contents results format.";
                            }

                            var sb = new StringBuilder();
                            int index = 1;
                            foreach (var item in results.EnumerateArray())
                            {
                                string title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "No Title" : "No Title";
                                string url = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                                string textContent = item.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "";
                                
                                sb.AppendLine($"[{index}] Title: {title}");
                                sb.AppendLine($"URL: {url}");
                                if (!string.IsNullOrEmpty(textContent))
                                {
                                    sb.AppendLine("Content:");
                                    sb.AppendLine(textContent);
                                }
                                sb.AppendLine();
                                index++;
                            }

                            return sb.ToString().TrimEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return $"Exa fetch exception occurred: {ex.Message}";
            }
        }

        private async Task<string> FetchUrlsWithoutApiKeyAsync(string[] urls, CancellationToken cancellationToken)
        {
            var sb = new StringBuilder();
            int index = 1;

            foreach (string rawUrl in urls)
            {
                string url = (rawUrl ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }

                sb.AppendLine($"[{index}] URL: {url}");

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        AddNoKeyWebHeaders(request);

                        using (var response = await _exaHttpClient.SendAsync(request, cancellationToken))
                        {
                            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                            if (!response.IsSuccessStatusCode)
                            {
                                sb.AppendLine($"Fetch failed ({response.StatusCode}): {LimitText(StripHtmlToText(responseBody), 1000)}");
                                sb.AppendLine();
                                index++;
                                continue;
                            }

                            string title = ExtractHtmlTitle(responseBody);
                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                sb.AppendLine($"Title: {title}");
                            }

                            sb.AppendLine("Content:");
                            sb.AppendLine(LimitText(StripHtmlToText(responseBody), NoKeyFetchMaxCharacters));
                            sb.AppendLine();
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Fetch exception occurred: {ex.Message}");
                    sb.AppendLine();
                }

                index++;
            }

            return sb.Length == 0
                ? "No-key web fetch failed: urls list is empty."
                : sb.ToString().TrimEnd();
        }

        private static void AddNoKeyWebHeaders(HttpRequestMessage request)
        {
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36 TxtAIEditor/1.0");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");
        }

        private static string DecodeDuckDuckGoHref(string href)
        {
            string decoded = WebUtility.HtmlDecode(href ?? string.Empty);
            string uddg = GetQueryStringValue(decoded, "uddg");
            if (!string.IsNullOrWhiteSpace(uddg))
            {
                return Uri.UnescapeDataString(uddg);
            }

            if (decoded.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + decoded;
            }

            if (decoded.StartsWith("/", StringComparison.Ordinal))
            {
                return "https://duckduckgo.com" + decoded;
            }

            return decoded;
        }

        private static string GetQueryStringValue(string url, string name)
        {
            int questionIndex = url.IndexOf('?');
            string query = questionIndex >= 0 ? url.Substring(questionIndex + 1) : url;
            string[] pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);

            foreach (string pair in pairs)
            {
                int equalsIndex = pair.IndexOf('=');
                string key = equalsIndex >= 0 ? pair.Substring(0, equalsIndex) : pair;
                if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return equalsIndex >= 0 ? pair.Substring(equalsIndex + 1) : string.Empty;
            }

            return string.Empty;
        }

        private static string ExtractHtmlTitle(string html)
        {
            var match = Regex.Match(html ?? string.Empty, "<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? StripHtmlToText(match.Groups["title"].Value) : string.Empty;
        }

        private static string StripHtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            string text = Regex.Replace(html, "<(script|style|svg|noscript)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, "</?(br|p|div|li|h[1-6]|tr|section|article|header|footer|main|nav)[^>]*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", " ", RegexOptions.Singleline);
            text = WebUtility.HtmlDecode(text);
            text = text.Replace('\u00A0', ' ');
            text = Regex.Replace(text, "[ \\t\\f\\v]+", " ");
            text = Regex.Replace(text, "\\s*\\n\\s*", "\n");
            text = Regex.Replace(text, "\\n{3,}", "\n\n");
            return text.Trim();
        }

        private static string LimitText(string text, int maxCharacters)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxCharacters)
            {
                return text;
            }

            return text.Substring(0, maxCharacters) + "...";
        }
    }
}
