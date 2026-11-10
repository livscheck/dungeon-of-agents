// Temporary home for the state enum; moves to Dungeon.Core once that library exists.
public enum AgentState
{
    Working,         // actively solving a project: walks the dungeon, torch lit
    AwaitingUser,    // struck down by a monster; waiting on the user's word
    Starved,         // budget exhausted: slow, dim, torch guttering
    Fallen,          // crashed: dead, no monster
    ReadyForTesting, // work done: stands still and prompts the user that it is ready for testing
    PrepForTesting,  // user approved: preparing the test files (the test-prep step)
    AwaitingTest,    // test files are in place: waiting for the user to test and report back
    Idle,            // not started yet: stands quietly
    Complete,        // merged to QA / production
}

public static class AgentStateInfo
{
    public static string Label(AgentState s) => s switch
    {
        AgentState.Working => "Working",
        AgentState.AwaitingUser => "AWAITING YOU",
        AgentState.Starved => "Starved",
        AgentState.Fallen => "Fallen",
        AgentState.ReadyForTesting => "READY FOR TESTING",
        AgentState.PrepForTesting => "Prepping test files",
        AgentState.AwaitingTest => "READY FOR LOCAL TEST",
        AgentState.Idle => "Not started",
        AgentState.Complete => "Complete",
        _ => s.ToString(),
    };

    /// <summary>States where the agent is waiting for the user, so the user is prompted about them.</summary>
    public static bool NeedsUser(AgentState s)
        => s == AgentState.AwaitingUser || s == AgentState.ReadyForTesting || s == AgentState.AwaitingTest;

    /// <summary>Short prompt shown when the player stands near an agent.</summary>
    public static string Hint(AgentState s) => s switch
    {
        AgentState.AwaitingUser => "[E] Speak  -  they need you",
        AgentState.ReadyForTesting => "[E] Speak  -  ready for testing",
        AgentState.AwaitingTest => "[E] Speak  -  test files are ready",
        _ => "[E] Speak",
    };

    /// <summary>Demo dialogue: what the agent says and the choices offered (placeholder until real agents drive this).</summary>
    public static (string text, string[] options) Dialogue(string agent, string project, AgentState s, string monster = "") => s switch
    {
        AgentState.Working => ($"I am deep in the work on {project}. Do not disturb me yet.", new string[0]),
        AgentState.AwaitingUser => (
            (monster == "" ? "Something struck me down." : $"The {monster} struck me down.") +
            $" I cannot rise or go on until you decide: may I proceed with {project}?",
            new[] { "Yes. Proceed.", "Not now." }),
        AgentState.Starved => ("The hunger gnaws at me... my strength is spent. Feed me, or let me rest.",
            new[] { "Feed them. (restore budget)", "Let them rest." }),
        AgentState.Fallen => ("...", new[] { "Raise them from the dead." }),
        AgentState.ReadyForTesting => ($"The work on {project} is done, and the changes are ready for you to test. Shall I prepare the test files?",
            new[] { "Yes. Prep for testing.", "Not yet." }),
        AgentState.PrepForTesting => ($"I am preparing the test files for {project}. One moment.", new string[0]),
        AgentState.AwaitingTest => ($"The test files for {project} are in place. Try them out and tell me what you find.",
            new[] { "Tests passed.", "Found problems." }),
        _ => ("...", new string[0]),
    };
}
