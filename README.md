# SnapStak Web Domain

A transformation engine that reproduces a website as production ready code across 10 web frameworks. Built on ConteX Law.

## The problem this solves

Design tools stop at a picture. Figma, Penpot and the rest hand you a mockup, and a developer then turns that mockup into code by hand. That manual stage is where the errors creep in, where the build drifts away from the design, and where the weeks go.

SnapStak Web Domain removes that stage. It captures the structure of a website that already exists and reproduces it as production ready source code in the framework you choose. The same input produces the same code on every run.

## What it does

Capture a target. SnapStak Web Domain takes the live structure of a website through its browser extension, or imports a design directly from Figma, Penpot or Canva. You then choose an output framework, and the engine produces production ready source code for it. The model writes the code. You name the framework.

The ten supported web frameworks are: React, Next.js, Vue, Nuxt, Angular, Svelte, SolidJS, Astro, Alpine, and Vanilla JavaScript.

## Built on ConteX Law

SnapStak Web Domain is not prompt engineering, and it is not a wrapper around a model's best guess. It is built on ConteX Law.

An AI model produces reliable code from a text prompt because code is structured, and structure is something a model can transcribe rather than invent. ConteX Law supplies that same structural completeness for the transformation task through four pillars:

- **Structure**: the shape of the domain, the elements that exist and the form a valid output must take.
- **Behaviour**: the rules that govern that structure, what is permitted, what depends on what, and what follows from what.
- **Influence**: the external authority the output must answer to, the sources and constraints that sit outside the model and outrank it.
- **Objective**: what the output must achieve, the target the other three pillars are held to.

These four pillars are the engine itself. In the code they are the StructureAgent, the BehaviourAgent, the Influence and Objective services, and the Constructor that assembles the result. With the four pillars supplied, the transformation is decided at the input rather than left to the model, which is why the output is reproducible and why it does not depend on which model runs underneath. The framework is model agnostic.

ConteX Law is documented in full in three papers on SSRN:

- ConteX Law (foundational paper): https://papers.ssrn.com/sol3/papers.cfm?abstract_id=6609519
- https://papers.ssrn.com/sol3/papers.cfm?abstract_id=6641679
- https://papers.ssrn.com/sol3/papers.cfm?abstract_id=6652458

## How it works

1. The browser extension captures the live structure of the target page, or a design is imported from Figma, Penpot or Canva.
2. The Structure Agent serialises that capture into a normalised structural model.
3. The Behaviour, Influence and Objective pillars are completed against that structure.
4. The Constructor sends the assembled four-pillar context to the model and receives the framework code.

The model calls go to OpenRouter using your own API key. The key and the prompt content are sent from the browser directly to OpenRouter. No SnapStak server sees your key or your prompts.

## Repository structure

- `SnapStak.Wasm.Client/` - the Blazor WebAssembly front end and the ConteX engine
- `SnapStak.Wasm.Server/` - the ASP.NET Core host, the Canva and Framer relays, mobile pairing and file transfer, and SkiaSharp rendering
- `SnapStak.Extension/` - the Chromium browser extension that captures the live structure of a target page
- `Figma snapstak-importer/` - a Figma plugin that exports a Figma design into SnapStak

## Prerequisites

- .NET 9 SDK
- Visual Studio 2022
- A Chromium based browser, for the capture extension
- Your own OpenRouter API key
- A SnapStak subscription code to activate the engine. The app validates the subscription on startup.

## Build and run

```
git clone https://github.com/SnapStak-AI/SnapStak-Web-Domain.git
cd SnapStak-Web-Domain
dotnet restore
dotnet build
dotnet run --project SnapStak.Wasm.Server
```

The server runs on http://localhost:5174. Load the capture extension into your browser as an unpacked extension from the `SnapStak.Extension/` folder, then open the app, enter your OpenRouter API key in Settings, capture a target, and choose a framework.

## Reproducibility

The output is deterministic at the input. The same four-pillar context produces the same code on every run, and produces it on any capable model rather than depending on one. That is the property ConteX Law exists to give, and it is the heart of why the framework can be trusted.

## Companion repository

For reproducing mobile applications as native code, see SnapStak Mobile: https://github.com/SnapStak-AI/SnapStak-Mobile

## Licence

SnapStak Web Domain is released under the GNU Affero General Public License v3.0. You are free to use, study, modify and distribute it, including commercially. If you modify it and either distribute it or run it as a network service, you must release your changes under the same licence. The work stays open.
