using UnityEngine;

public class SharedCoroutineStarter : MonoBehaviour {
	public static MonoBehaviour instance;

	public static void Init() {
		if(instance != null) return;
		instance = new GameObject().AddComponent<SharedCoroutineStarter>();
	}

	void Awake() {
		GameObject.DontDestroyOnLoad(gameObject);
	}
}
