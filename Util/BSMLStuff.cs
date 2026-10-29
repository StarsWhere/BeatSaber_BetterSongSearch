using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Parser;
using HMUI;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace BetterSongSearch.Util {
	static class BSMLStuff {
		public static void InitializeScrollViews(GameObject root) {
			var platform = Resources.FindObjectsOfTypeAll<ScrollView>().Select(x => x._platformHelper).FirstOrDefault(x => x != null);
			if(platform != null) foreach(var scroll in root.GetComponentsInChildren<ScrollView>(true)) IPA.Utilities.ReflectionUtil.SetField(scroll,"_platformHelper",platform);
            foreach(var table in root.GetComponentsInChildren<TableView>(true)) {
                var scroll=table.GetComponent<ScrollView>();
                if(scroll!=null && scroll._contentRectTransform!=null) table._contentTransform=scroll._contentRectTransform;
            }
		}
		public static BSMLParserParams InitSplitView(ref BSMLParserParams pparams, GameObject targetGameObject, object host, string viewName = null) {
			if(pparams != null)
				return pparams;

			if(viewName == null)
				viewName = host.GetType().Name;

			var previous=targetGameObject.transform.Cast<Transform>().ToArray();
			pparams = BSMLParser.Instance.Parse(Utilities.GetResourceContent(Assembly.GetExecutingAssembly(), $"BetterSongSearch.UI.Views.SplitViews.{viewName}.bsml"), targetGameObject, host);
            foreach(Transform child in targetGameObject.transform) if(!previous.Contains(child)) BssUi.Settings(child.gameObject,72);
			return pparams;
		}

		static GameObject scrollBar = null;

		public static GameObject GetScrollbarForTable(GameObject table, Transform targetContainer) {
			if(scrollBar == null)
				scrollBar = Resources.FindObjectsOfTypeAll<VerticalScrollIndicator>().FirstOrDefault(x => x.enabled)?.transform.parent?.gameObject;

			if(scrollBar == null)
				return null;

			var sw = table.GetComponentInChildren<ScrollView>();

			if(sw == null)
				return null;

			var listScrollBar = GameObject.Instantiate(scrollBar, targetContainer, false);
			listScrollBar.SetActive(true);
			var vsi = listScrollBar.GetComponentInChildren<VerticalScrollIndicator>(true);

			sw._verticalScrollIndicator = vsi;

			var buttoneZ = listScrollBar.GetComponentsInChildren<NoTransitionsButton>(true).OrderByDescending(x => x.gameObject.name == "UpButton").ToArray();
			if(buttoneZ.Length == 2) {
				sw._pageUpButton = buttoneZ[0];
				sw._pageDownButton = buttoneZ[1];

				buttoneZ[0].onClick.AddListener(sw.PageUpButtonPressed);
				buttoneZ[1].onClick.AddListener(sw.PageDownButtonPressed);
			}

			// I dont know WHY I need do do this, but if I dont the scrollbar wont work with the added modal.
			foreach(Transform x in listScrollBar.transform) {
				foreach(var y in x.GetComponents<Behaviour>())
					y.enabled = true;
			}

			sw.Update();
			sw.gameObject.AddComponent<RefreshScrolbarOnFirstLoad>();

			return scrollBar;
		}

		class RefreshScrolbarOnFirstLoad : MonoBehaviour {
			void OnEnable() => StartCoroutine(dorefresh());

			IEnumerator dorefresh() {
				yield return null;
				var sv = gameObject.GetComponent<ScrollView>();

				if(sv == null || sv._verticalScrollIndicator == null)
					yield break;

				sv._verticalScrollIndicator.RefreshHandle();
			}
		}
	}
}
