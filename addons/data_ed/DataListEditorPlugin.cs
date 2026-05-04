#if TOOLS

using Godot;

[Tool]
public partial class DataListEditorPlugin : EditorPlugin
{
	private DataListEditorInspector _inspector;

	public override void _EnterTree()
	{
		_inspector = new DataListEditorInspector();
		AddInspectorPlugin(_inspector);
	}

	public override void _ExitTree()
	{
		RemoveInspectorPlugin(_inspector);
	}
}

#endif
