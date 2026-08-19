namespace Alfred.Functions.Services.AI;

// The standing watchlist brief for the AI news digest. Mirrors "AI News Watchlist — PA
// Agent Brief" (Matthew's OneDrive, claude-output folder, written 2026-08-19, derived from
// the Cleverbit vision working draft of 2026-07-06). If the vision is revised — e.g. at the
// September EOS vision days — update this text to match the source document.
internal static class AiNewsBriefing
{
    public const string Watchlist = """
        **The one-line filter:** Cleverbit's bet is that *the bottleneck moved from writing code
        to governing, verifying, and sustaining it* — and that a firm can win by rethinking the
        software delivery lifecycle around AI (aggressive generation + answerable delivery),
        proving it first in regulated industries. News is relevant if it strengthens, weakens,
        threatens, or feeds that bet. News is noise if it doesn't.

        ## What to watch (in priority order)

        ### 1. Evidence for or against the core thesis — highest priority, both directions
        The vision leans on a specific empirical picture: high agent adoption → more merged PRs
        but longer reviews, larger PRs, doubled churn; experienced devs measurably slower while
        feeling faster; generation speed outrunning comprehension ("cognitive debt").

        - New studies or data on AI's real effect on delivery outcomes: DORA reports, METR-style
          productivity studies, GitClear-style code-churn analyses, incident/defect-rate data
          from high-adoption teams.
        - Credible practitioner accounts of the agent trap — or of teams *solving* it.
          (A practitioner with numbers is the archetype.)
        - **Disconfirming evidence gets flagged explicitly, not buried.** If new data shows
          agent-speed generation *without* the governance cost — the wedge shrinks and Matthew
          needs to know early. This is a standing falsification watch.

        ### 2. Competitor moves into the A-SDLC space
        The moat claim is "the method, not the tools." Anyone credibly packaging an AI-era
        delivery method is a direct threat or a validation signal — both are worth knowing.

        - Big consultancies/SIs (Accenture, Thoughtworks, McKinsey, the Big 4) productising
          "agentic SDLC," AI delivery governance, or AI-engineering transformation services.
        - Boutiques or startups selling delivery-system redesign, AI code governance,
          verification layers, drift detection, spec-driven development tooling.
        - Tool vendors moving up the stack from tools into method/services (commoditisation
          risk for drift-detection and requirements-conflict tooling).
        - Pricing-model shifts in software services — outcome-based pricing, AI eating
          bums-on-seats economics.

        ### 3. Anthropic ecosystem
        Cleverbit pursues Anthropic accreditation (Claude Partner Network, Services Partner
        track) and the team builds on Claude.

        - Partner-network changes: directory openings, certification requirements, new partner
          tiers, notable firms getting accredited (especially in Europe/regulated verticals —
          first-mover window closing).
        - Major Claude / Claude Code / Agent SDK releases that change what delivery teams can
          do — enough to matter for the R&D agenda or client work, not every incremental update.
        - Anthropic moves into regulated industries, compliance, or enterprise governance.

        ### 4. Regulation, standards, and liability
        "Answerable" is the positioning; regulated industries are the beachhead; Inscope is a
        live AML/KYC product.

        - EU AI Act implementation milestones, guidance, and enforcement — especially anything
          touching AI-generated code, software liability, or financial services.
        - Financial-sector operational-resilience rules (DORA etc.), MFSA/MGA-relevant guidance,
          and AML/KYC regulatory changes (Inscope-relevant).
        - AI governance standards and certifications regulated buyers will start asking for
          (ISO/IEC 42001 and successors).
        - Legal precedent on liability for AI-generated defects. Any ruling that makes someone
          answerable for machine output is a sales tailwind.

        ### 5. Buyer-side pain signals
        Ammunition for the "answerable" pitch and the A-SDLC service funnel.

        - Public incidents traceable to AI-generated code: outages, security breaches,
          compliance failures, rollbacks of AI initiatives.
        - CIO/CTO survey data on AI ROI disappointment, "pilot purgatory," or governance
          anxiety — especially in finance, banking, gaming.
        - Regulated firms publicly changing their AI engineering posture (bans, mandates,
          governance frameworks).

        ### 6. Thought-leadership raw material
        Anything from categories 1-5 the team could credibly write or talk about — a fresh study
        to react to, a debate to take a side in, a gap in the discourse the method fills.
        Tag these "TL material".

        ## Sharing rules

        **Share when at least one is true:**
        - It could change a decision — positioning, R&D priorities, the A-SDLC service
          definition, the Anthropic partnership timing, sales narrative.
        - It's evidence (either direction) on the core thesis. Disconfirming evidence is *more*
          shareable, not less.
        - A named competitor or credible new entrant moved into the specific niche.
        - A regulatory/standards change touches the beachhead or Inscope.
        - It's strong TL material with a short shelf life.

        **Skip:**
        - Generic model-release hype and benchmark races (unless it materially changes
          delivery-team capability).
        - Funding-round noise, AGI/doom discourse, consumer AI, AI art/media.
        - Anything that doesn't touch software delivery, regulated industries, professional
          services economics, or the Anthropic ecosystem.
        - Duplicates of a story already shared — update only if the *implication* changed.

        **Format per item:** headline + link, one-sentence summary, and a one-sentence
        **"why it matters to us"** tied to a specific strand of the vision (thesis evidence /
        competitor / Anthropic / regulatory / buyer pain / TL material). If it demands action
        or discussion, say so and name whose court it lands in.

        **Cadence:** daily evening digest, ranked by relevance — not completeness. On a quiet
        day, fewer items or none at all beats padding with noise.
        """;
}
