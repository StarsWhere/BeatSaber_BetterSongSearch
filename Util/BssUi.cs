using System.Linq;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.Components;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BetterSongSearch.Util {
    static class BssUi {
        public static void Settings(GameObject root,float width = 108f) {
            foreach(var setting in root.GetComponentsInChildren<GenericSetting>(true)) Setting(setting,width,50);
            Style(root);
        }
        public static void Setting(GenericSetting setting,float width,float labelWidth,float labelSize=2.6f,float valueSize=2.4f) {
            var root=setting.GetComponent<ExternalComponents>()?.Components.OfType<LayoutElement>().FirstOrDefault()?.gameObject ?? setting.gameObject;
            var layout = root.GetComponent<LayoutElement>() ?? root.AddComponent<LayoutElement>();
            layout.layoutPriority = 10;
            layout.minWidth = layout.preferredWidth = width; layout.flexibleWidth = 0;
            layout.minHeight = layout.preferredHeight = 8; layout.flexibleHeight = 0;
            var rootRect=(RectTransform)root.transform;
            rootRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,width); rootRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,8);
            var fitter = root.GetComponent<ContentSizeFitter>();
            if(fitter != null) { fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained; fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained; }
            var label = root.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(x => x.name == "Label");
            if(label != null) {
                label.gameObject.SetActive(labelWidth > 0);
                label.fontSize = labelSize;
                label.enableAutoSizing=false;
                label.alignment=TextAlignmentOptions.MidlineLeft;
                var labelLayout = label.GetComponent<LayoutElement>() ?? label.gameObject.AddComponent<LayoutElement>();
                labelLayout.layoutPriority=10; labelLayout.minWidth=labelLayout.preferredWidth=labelWidth; labelLayout.flexibleWidth=0;
                label.rectTransform.sizeDelta = new Vector2(labelWidth,8);
                label.rectTransform.anchorMin=label.rectTransform.anchorMax=new Vector2(0,.5f);
                label.rectTransform.pivot=new Vector2(0,.5f); label.rectTransform.anchoredPosition=new Vector2(4,0);
            }
            if(setting is DropDownListSetting) {
                var dropdown=(RectTransform)setting.transform;
                dropdown.anchorMin=dropdown.anchorMax=new Vector2(1,.5f); dropdown.pivot=new Vector2(1,.5f); dropdown.anchoredPosition=Vector2.zero;
                dropdown.sizeDelta=new Vector2(width-labelWidth-4,8);
            }
            var button = root.GetComponentsInChildren<Button>(true).FirstOrDefault(x => x.name == "DropDownButton");
            if(button != null) {
                float valueWidth = width-labelWidth-4;
                ((RectTransform)button.transform).sizeDelta = new Vector2(valueWidth,8);
                var rect=(RectTransform)button.transform;
                rect.anchorMin=rect.anchorMax=new Vector2(1,.5f); rect.pivot=new Vector2(1,.5f); rect.anchoredPosition=Vector2.zero;
                var buttonLayout = button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
                buttonLayout.layoutPriority=10; buttonLayout.minWidth=buttonLayout.preferredWidth=valueWidth; buttonLayout.flexibleWidth=0;
            }
            foreach(var text in root.GetComponentsInChildren<TMP_Text>(true)) { text.enableAutoSizing=false; text.fontSize = text.name == "Label" ? labelSize : valueSize; }
        }
        public static void Text(TMP_Text text,float width,float height) {
            var layout=text.GetComponent<LayoutElement>() ?? text.gameObject.AddComponent<LayoutElement>();
            layout.layoutPriority=10; layout.minWidth=layout.preferredWidth=width; layout.flexibleWidth=width==0 ? 1 : 0;
            layout.minHeight=layout.preferredHeight=height; layout.flexibleHeight=0;
            var fitter=text.GetComponent<ContentSizeFitter>();
            if(fitter!=null) { fitter.horizontalFit=ContentSizeFitter.FitMode.Unconstrained; fitter.verticalFit=ContentSizeFitter.FitMode.Unconstrained; }
        }
        public static void Button(Button button,float width,float height) {
            var layout=button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
            layout.layoutPriority=10;layout.minWidth=layout.preferredWidth=width;layout.minHeight=layout.preferredHeight=height;
            layout.flexibleWidth=layout.flexibleHeight=0;
        }
        public static void Style(GameObject root) {
            BssTypography.Apply(root);
            foreach(var image in root.GetComponentsInChildren<ImageView>(true)) if(image.name == "Underline") image.gameObject.SetActive(false);
        }
    }
}
