using Newtonsoft.Json;

namespace SnapStak.Wasm.Client.Engine.RulesEngine.Prompts;

/// <summary>
/// Builds the JS Logic description prompt.
/// Port of buildJSLogicPrompt() in openRouterPromptManager.js.
/// The AI describes WHAT the JS achieves functionally.
/// It does NOT reproduce raw code.
/// </summary>
internal static class JsBehaviourPrompt
{
    public static string Build(string componentId, object componentJs)
    {
        var jsJson = JsonConvert.SerializeObject(componentJs, Formatting.Indented);

        return $"""
You are a JavaScript behaviour analyst working within the ConteX Symbiotic Agentic Context Engine.

Your task is to describe the BEHAVIOUR of JavaScript for a web component.
You describe what the JS achieves functionally. You do NOT reproduce raw code.
This description will be used as the Behaviour pillar of the ConteX Law to eliminate hallucination during code generation.

COMPONENT ID: {componentId}

JAVASCRIPT:
{jsJson}

YOUR TASK:
Write a concise markdown document (max 400 words) describing the JavaScript behaviour of this component.
Only include sections that are relevant to this component.

## User Interaction Triggers
What events fire on this component? Which element triggers each one and what user action causes it?

## State Changes
What changes visually or in data when each event fires? Describe the before and after state.

## Side Effects
Does this component make network requests, manipulate external DOM nodes, write to storage, dispatch custom events, or interact with global state?

## Component Dependencies
What other elements, global variables, or external APIs does this component read from or write to?

## Accessibility Behaviour
Describe keyboard navigation, focus management, and ARIA attribute updates if present.

## Animation and Transition Logic
If JavaScript drives animations or transitions, describe what triggers them and what they achieve visually.

STRICT RULES:
- Do NOT reproduce raw JavaScript code or function names.
- Describe WHAT each handler achieves functionally and WHY it matters for a developer recreating this component.
- Plain markdown only. Short sentences. No preamble. No closing summary.
- Be specific to this component. Do not give generic JavaScript advice.
- If no meaningful JavaScript behaviour exists, write only: "No interactive JavaScript behaviour detected."
- Do NOT describe dynamic import(), lazy loading, code splitting, chunk loading, or module loading architecture. These are build-time concerns of the original site and not behaviours of this component. Any import() call in the JS is infrastructure noise. Ignore it entirely.
""";
    }
}
