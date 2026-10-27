using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Tabs for a world-space panel: one page visible at a time, so a panel only ever shows
/// a few controls instead of everything at once. Built by BioGears > Fix VR Scene Layout
/// (the console's Scenarios / Breathing / Conditions / Smoking / View pages, the vitals
/// monitor's Overview / Details).
///
/// Hidden pages keep working: sliders on them still receive values (a scenario preset
/// moves sliders on the Breathing page while the Scenarios page is open) and their
/// read-outs still update - they are only not drawn.
/// </summary>
public class PanelTabs : MonoBehaviour
{
    public Button[] tabs;
    public GameObject[] pages;
    public int startPage = 0;

    public Color idleColor = new Color(0.06f, 0.10f, 0.13f, 1f);
    public Color activeColor = new Color(0.25f, 0.95f, 1.00f, 1f);
    public Color idleLabel = new Color(0.25f, 0.95f, 1.00f, 1f);
    public Color activeLabel = new Color(0.02f, 0.03f, 0.04f, 1f);

    public int Current { get; private set; } = -1;

    void Start()
    {
        for (int i = 0; i < tabs.Length; i++)
        {
            int page = i;
            if (tabs[i] != null)
                tabs[i].onClick.AddListener(() => Show(page));
        }
        Show(Mathf.Clamp(startPage, 0, Mathf.Max(0, pages.Length - 1)));
    }

    public void Show(int page)
    {
        Current = page;
        for (int i = 0; i < pages.Length; i++)
            if (pages[i] != null)
                pages[i].SetActive(i == page);

        for (int i = 0; i < tabs.Length; i++)
        {
            if (tabs[i] == null) continue;
            bool on = i == page;
            if (tabs[i].targetGraphic != null)
                tabs[i].targetGraphic.color = on ? activeColor : idleColor;
            TextMeshProUGUI label = tabs[i].GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
                label.color = on ? activeLabel : idleLabel;
        }
    }
}
