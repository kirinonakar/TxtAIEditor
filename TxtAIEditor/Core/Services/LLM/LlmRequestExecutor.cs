using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Core.Services.LLM
{
    internal sealed class LlmRequestExecutor
    {
        private readonly ISettingsService _settingsService;
        private readonly LlmCredentialStore _credentialStore;
        private readonly ILocalizationService _localizationService;
        private readonly LlmTokenUsageTracker _tokenUsageTracker;

        public LlmRequestExecutor(
            ISettingsService settingsService,
            LlmCredentialStore credentialStore,
            ILocalizationService localizationService,
            LlmTokenUsageTracker tokenUsageTracker)
        {
            _settingsService = settingsService;
            _credentialStore = credentialStore;
            _localizationService = localizationService;
            _tokenUsageTracker = tokenUsageTracker;
        }

        public async Task<string> ExecuteAsync(string systemPrompt, string userContent, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, bool allowVisionFallback = false, Func<string, Task<bool>>? onVisionFallbackResult = null, Func<Task>? onNativeToolCall = null, Func<string, Task>? onApiType = null)
        {
            return await ExecuteAsync(_settingsService.CurrentSettings, systemPrompt, userContent, onChunk, cancellationToken, attachments, onReasoning, tools, onUsage, allowVisionFallback, onVisionFallbackResult, onNativeToolCall, onApiType);
        }

        public async Task<string> ExecuteAsync(EditorSettings settings, string systemPrompt, string userContent, Func<string, Task>? onChunk = null, CancellationToken cancellationToken = default, IReadOnlyList<LlmMessageAttachment>? attachments = null, Func<string, Task>? onReasoning = null, IReadOnlyList<LlmTool>? tools = null, Func<LlmTokenUsage, Task>? onUsage = null, bool allowVisionFallback = false, Func<string, Task<bool>>? onVisionFallbackResult = null, Func<Task>? onNativeToolCall = null, Func<string, Task>? onApiType = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string providerName = settings.LlmProvider;
            string apiKey = await _credentialStore.GetApiKeyAsync(providerName);
            bool isOpenCodeZen = providerName.Equals("OpenCode Zen", StringComparison.OrdinalIgnoreCase) ||
                                 providerName.Equals("OpenCodeZen", StringComparison.OrdinalIgnoreCase) ||
                                 providerName.Equals("Zen", StringComparison.OrdinalIgnoreCase);
            bool requiresApiKey = !providerName.Equals("LM Studio", StringComparison.OrdinalIgnoreCase) &&
                                    !providerName.Equals("LMStudio", StringComparison.OrdinalIgnoreCase) &&
                                    !providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase) &&
                                    !providerName.Equals("OpenAI OAuth", StringComparison.OrdinalIgnoreCase) &&
                                    !providerName.Equals("OpenAIOAuth", StringComparison.OrdinalIgnoreCase) &&
                                    !isOpenCodeZen;
            if (providerName.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                string customEndpoint = settings.LlmEndpoint?.Trim() ?? string.Empty;
                bool isLocalEndpoint = customEndpoint.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                       customEndpoint.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
                requiresApiKey = !isLocalEndpoint;
            }

            if (requiresApiKey && string.IsNullOrEmpty(apiKey))
            {
                return _localizationService.GetString("LlmErrorNoApiKeyOrToken", "에러: 해당 LLM API Key가 자격 증명 관리자에 등록되어 있지 않습니다. 설정을 열어 자격 증명을 먼저 저장해 주십시오.");
            }

            // OpenCode Zen free models can be called without an API key; the gateway accepts the public stub token.
            if (isOpenCodeZen && string.IsNullOrEmpty(apiKey))
            {
                apiKey = "public";
            }

            ILLMProvider provider = providerName.ToLower() switch
            {
                "gemini" => new GeminiProvider(_localizationService, settings.LlmAgentVerbose, settings.LlmThinkingLevel, providerName),
                "openai oauth" => new OpenAIProvider(_localizationService, isOAuth: true, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName),
                "openaioauth" => new OpenAIProvider(_localizationService, isOAuth: true, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName),
                "cerebras" => new CerebrasProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "upstage" => new UpstageProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "openrouter" => new OpenRouterProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "lm studio" => new LMStudioProvider(_localizationService, settings.LlmThinkingLevel),
                "lmstudio" => new LMStudioProvider(_localizationService, settings.LlmThinkingLevel),
                "ollama" => new OllamaProvider(_localizationService, isCloud: false, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName),
                "ollama cloud" => new OllamaProvider(_localizationService, isCloud: true, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName),
                "ollamacloud" => new OllamaProvider(_localizationService, isCloud: true, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName),
                "unsloth desktop" => new UnslothProvider(_localizationService, settings.LlmThinkingLevel),
                "unslothdesktop" => new UnslothProvider(_localizationService, settings.LlmThinkingLevel),
                "unsloth" => new UnslothProvider(_localizationService, settings.LlmThinkingLevel),
                "opencode go" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "opencodego" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "go" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "opencode zen" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "opencodezen" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "zen" => new GoProvider(_localizationService, settings.LlmThinkingLevel, providerName),
                "custom" => new OpenAIProvider(_localizationService, isOAuth: false, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName),
                _ => new OpenAIProvider(_localizationService, isOAuth: false, thinkingLevel: settings.LlmThinkingLevel, providerName: providerName)
            };

            bool emittedOutput = false;
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
                        settings.LlmEndpoint ?? string.Empty,
                        apiKey,
                        settings.LlmModel,
                        systemPrompt,
                        userContent,
                        async chunk =>
                        {
                            emittedOutput = true;
                            fullResponse.Append(chunk);
                            await onChunk(chunk);
                        },
                        cancellationToken,
                        attachments,
                        async reasoningChunk =>
                        {
                            reasoningResponse.Append(reasoningChunk);
                            if (onReasoning != null)
                            {
                                await onReasoning(reasoningChunk);
                            }
                            else if (settings.LlmAgentVerbose)
                            {
                                emittedOutput = true;
                                fullResponse.Append(reasoningChunk);
                                await onChunk(reasoningChunk);
                            }
                        },
                        tools,
                        onProviderUsage,
                        onNativeToolCall,
                        onApiType
                    );
                    if (observedUsage != null)
                    {
                        _tokenUsageTracker.Record(observedUsage);
                    }
                    return fullResponse.Length > 0 ? fullResponse.ToString() : reasoningResponse.ToString();
                }
                else
                {
                    string result = await provider.GenerateCompletionAsync(
                        settings.LlmEndpoint ?? string.Empty,
                        apiKey,
                        settings.LlmModel,
                        systemPrompt,
                        userContent,
                        cancellationToken,
                        attachments,
                        tools,
                        onProviderUsage,
                        onNativeToolCall,
                        onApiType
                    );
                    if (observedUsage != null)
                    {
                        _tokenUsageTracker.Record(observedUsage);
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
                if (allowVisionFallback &&
                    !emittedOutput &&
                    attachments?.Count > 0 &&
                    TryCreateVisionFallbackSettings(settings, out EditorSettings? fallbackSettings))
                {
                    string fallbackTaskContent =
                        userContent +
                        "\n\n[Vision fallback task]\n" +
                        "The primary model could not process the attached image data. " +
                        "Inspect the attached image using all prior conversation, workspace, selection, and tool-result context above. " +
                        "Return a concise, factual visual analysis relevant to the original task. " +
                        "If the original task requires an immediate tool action based on that analysis, emit the normal tool call after the analysis; " +
                        "otherwise stop after the analysis.";
                    string fallbackAnalysis = await ExecuteAsync(
                        fallbackSettings,
                        systemPrompt,
                        fallbackTaskContent,
                        onChunk: _ => Task.CompletedTask,
                        cancellationToken,
                        attachments,
                        onReasoning: _ => Task.CompletedTask,
                        tools,
                        onUsage,
                        allowVisionFallback: false,
                        onVisionFallbackResult: null,
                        onNativeToolCall: onNativeToolCall,
                        onApiType: onApiType);

                    if (IsLlmErrorResponse(fallbackAnalysis))
                    {
                        return fallbackAnalysis;
                    }

                    string fallbackContext =
                        $"[Vision fallback result: {fallbackSettings.LlmProvider} / {fallbackSettings.LlmModel}]\n" +
                        fallbackAnalysis.Trim();
                    bool forwardFallbackResponse = onVisionFallbackResult != null &&
                        await onVisionFallbackResult(fallbackContext);
                    if (forwardFallbackResponse)
                    {
                        return fallbackAnalysis;
                    }

                    string originalModelContent =
                        userContent +
                        "\n\n" + fallbackContext +
                        "\n\n[Continuation after vision fallback]\n" +
                        "Use the visual analysis above as the result of the image read, preserve its surrounding context, " +
                        "and continue the original task with the normal tool-call or final-answer behavior.";
                    string continuationResponse = await ExecuteAsync(
                        settings,
                        systemPrompt,
                        originalModelContent,
                        onChunk,
                        cancellationToken,
                        attachments: null,
                        onReasoning,
                        tools,
                        onUsage,
                        allowVisionFallback: false,
                        onVisionFallbackResult: null,
                        onNativeToolCall: onNativeToolCall,
                        onApiType: onApiType);
                    if (IsLlmEmptyResponse(continuationResponse))
                    {
                        if (onChunk != null)
                        {
                            await onChunk(fallbackAnalysis);
                        }

                        return fallbackAnalysis;
                    }

                    return continuationResponse;
                }

                string errorPrefix = _localizationService.GetString("LlmErrorCommunicationPrefix", "AI 통신 오류가 발생했습니다: ");
                return $"{errorPrefix}{ex.Message}";
            }
        }

        private bool IsLlmErrorResponse(string response)
        {
            if (IsLlmEmptyResponse(response))
            {
                return true;
            }

            string communicationErrorPrefix = _localizationService.GetString(
                "LlmErrorCommunicationPrefix",
                "AI 통신 오류가 발생했습니다: ");
            string missingCredentialError = _localizationService.GetString(
                "LlmErrorNoApiKeyOrToken",
                "에러: 해당 LLM API Key가 자격 증명 관리자에 등록되어 있지 않습니다. 설정을 열어 자격 증명을 먼저 저장해 주십시오.");
            return response.StartsWith(communicationErrorPrefix, StringComparison.Ordinal) ||
                response.Equals(missingCredentialError, StringComparison.Ordinal);
        }

        private bool IsLlmEmptyResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return true;
            }

            string emptyResponse = _localizationService.GetString(
                "LlmErrorEmptyResponse",
                "AI로부터 빈 응답을 수신했습니다.");
            string geminiEmptyResponse = _localizationService.GetString(
                "GeminiErrorEmptyResponse",
                "Gemini AI로부터 빈 응답을 수신했습니다.");
            string lmStudioEmptyResponse = _localizationService.GetString(
                "LmStudioErrorEmptyResponse",
                "LM Studio로부터 빈 응답을 수신했습니다.");
            string unslothEmptyResponse = _localizationService.GetString(
                "UnslothErrorEmptyResponse",
                "Unsloth Desktop로부터 빈 응답을 수신했습니다.");
            return response.Equals(emptyResponse, StringComparison.Ordinal) ||
                response.Equals(geminiEmptyResponse, StringComparison.Ordinal) ||
                response.Equals(lmStudioEmptyResponse, StringComparison.Ordinal) ||
                response.Equals(unslothEmptyResponse, StringComparison.Ordinal);
        }

        private static bool TryCreateVisionFallbackSettings(
            EditorSettings settings,
            out EditorSettings fallbackSettings)
        {
            string fallbackProvider = settings.LlmVisionFallbackProvider?.Trim() ?? string.Empty;
            string fallbackModel = settings.LlmVisionFallbackModel?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fallbackProvider) ||
                string.IsNullOrWhiteSpace(fallbackModel) ||
                (fallbackProvider.Equals(settings.LlmProvider, StringComparison.OrdinalIgnoreCase) &&
                    fallbackModel.Equals(settings.LlmModel, StringComparison.OrdinalIgnoreCase)))
            {
                fallbackSettings = null!;
                return false;
            }

            bool sameProvider = fallbackProvider.Equals(settings.LlmProvider, StringComparison.OrdinalIgnoreCase);
            string fallbackThinkingLevel = settings.LlmVisionFallbackThinkingLevel;
            if (string.IsNullOrWhiteSpace(fallbackThinkingLevel))
            {
                fallbackThinkingLevel = "default";
            }
            fallbackSettings = new EditorSettings
            {
                Language = settings.Language,
                LlmProvider = fallbackProvider,
                LlmEndpoint = sameProvider
                    ? settings.LlmEndpoint
                    : SettingsLlmModelCatalog.GetDefaultEndpoint(fallbackProvider, settings.LlmEndpoint),
                LlmModel = fallbackModel,
                LlmThinkingLevel = fallbackThinkingLevel,
                LlmAgentVerbose = settings.LlmAgentVerbose,
                LlmRetainThinking = settings.LlmRetainThinking,
                LlmSourceLanguage = settings.LlmSourceLanguage,
                LlmTargetLanguage = settings.LlmTargetLanguage,
                LlmVisionFallbackProvider = fallbackProvider,
                LlmVisionFallbackModel = fallbackModel
            };
            return true;
        }
    }
}
