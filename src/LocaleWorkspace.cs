namespace Leaves_Questrandomizer;

// One profile's in-progress locale EDITS: locale id -> text. Built fresh each time that profile's
// quests get generated (see Questrandomizer.EnsureProfileDataGenerated).
//
// The mod is ENGLISH-ONLY, so there is NO language dimension here - the same text is what every
// language ends up serving. Generation stores this sparse set once per profile; the served locale for
// a given language is built ON DEMAND by overlaying these edits onto that language's vanilla locale
// (see GetLocaleDbPatch). Deliberately sparse - only the ids we actually changed (quest names/
// descriptions/generated condition text), NOT a full copy of any locale dictionary.
public class LocaleWorkspace
{
    // locale id -> new text.
    public Dictionary<string, string> Changes { get; } = new();

    public void Edit(string id, string text)
    {
        Changes[id] = text;
    }
}
