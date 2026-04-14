using System;
using System.Globalization;

public enum CVarType
{
	None,
	Int,
	Float,
	Bool,
	String
}

public class CVar
{
	public string Name { get; private set; }
	public CVarType Type { get; private set; }
	public Action<CVar> OnChanged;

	protected string _value;

	public CVar(string name, CVarType type, string defaultValue, Action<CVar> onChanged = null)
	{
		Name = name;
		Type = type;
		_value = defaultValue;
		OnChanged = onChanged;
		CVarRegistry.Register(this);
	}

	public CVar(string name, Action<CVar> onExecuted)
	{
		Name = name;
		Type = CVarType.None;
		_value = null;
		OnChanged = onExecuted;
		CVarRegistry.Register(this);
	}

	public int GetInt()
	{
		return int.Parse(_value);
	}

	public float GetFloat()
	{
		return float.Parse(_value, CultureInfo.InvariantCulture);
	}

	public bool GetBool()
	{
		return _value == "1" || _value.Equals("true", StringComparison.OrdinalIgnoreCase);
	}

	public string GetString()
	{
		return _value;
	}

	public virtual string Set(string newValue)
	{
		if (Type == CVarType.None)
		{
			return "Cannot set value on an action CVar.";
		}

		if (!Validate(newValue))
		{
			return $"Invalid value '{newValue}' for {Type} CVar '{Name}'.";
		}

		_value = newValue;
		OnChanged?.Invoke(this);
		return null;
	}

	public void Execute()
	{
		OnChanged?.Invoke(this);
	}

	private bool Validate(string value)
	{
		switch (Type)
		{
			case CVarType.Int:
				return int.TryParse(value, out _);
			case CVarType.Float:
				return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
			case CVarType.Bool:
				return value == "0" || value == "1"
					|| value.Equals("true", StringComparison.OrdinalIgnoreCase)
					|| value.Equals("false", StringComparison.OrdinalIgnoreCase);
			case CVarType.String:
				return true;
			default:
				return false;
		}
	}

	public override string ToString()
	{
		if (Type == CVarType.None)
		{
			return $"{Name} (action)";
		}

		return $"{Name} = {_value}";
	}
}

public class CVarInt : CVar
{
	private int _cached;

	public int Value
	{
		get => _cached;
		set { _cached = value; _value = value.ToString(); OnChanged?.Invoke(this); }
	}

	public CVarInt(string name, int defaultValue, Action<CVar> onChanged = null)
		: base(name, CVarType.Int, defaultValue.ToString(), onChanged)
	{
		_cached = defaultValue;
	}

	public override string Set(string newValue)
	{
		string error = base.Set(newValue);
		if (error == null)
		{
			_cached = int.Parse(_value);
		}

		return error;
	}
}

public class CVarFloat : CVar
{
	private float _cached;

	public float Value
	{
		get => _cached;
		set { _cached = value; _value = value.ToString(CultureInfo.InvariantCulture); OnChanged?.Invoke(this); }
	}

	public CVarFloat(string name, float defaultValue, Action<CVar> onChanged = null)
		: base(name, CVarType.Float, defaultValue.ToString(CultureInfo.InvariantCulture), onChanged)
	{
		_cached = defaultValue;
	}

	public override string Set(string newValue)
	{
		string error = base.Set(newValue);
		if (error == null)
		{
			_cached = float.Parse(_value, CultureInfo.InvariantCulture);
		}

		return error;
	}
}

public class CVarBool : CVar
{
	private bool _cached;

	public bool Value
	{
		get => _cached;
		set { _cached = value; _value = value ? "1" : "0"; OnChanged?.Invoke(this); }
	}

	public CVarBool(string name, bool defaultValue, Action<CVar> onChanged = null)
		: base(name, CVarType.Bool, defaultValue ? "1" : "0", onChanged)
	{
		_cached = defaultValue;
	}

	public override string Set(string newValue)
	{
		string error = base.Set(newValue);
		if (error == null)
		{
			_cached = _value == "1" || _value.Equals("true", StringComparison.OrdinalIgnoreCase);
		}

		return error;
	}
}

public class CVarString : CVar
{
	public string Value
	{
		get => _value;
		set { _value = value; OnChanged?.Invoke(this); }
	}

	public CVarString(string name, string defaultValue, Action<CVar> onChanged = null)
		: base(name, CVarType.String, defaultValue, onChanged)
	{
	}

	public override string Set(string newValue)
	{
		if (newValue == "\"\"")
		{
			newValue = "";
		}
		return base.Set(newValue);
	}
}
