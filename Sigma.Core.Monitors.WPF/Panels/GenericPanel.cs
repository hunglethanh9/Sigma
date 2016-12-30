﻿using System.Windows;

namespace Sigma.Core.Monitors.WPF.Panels
{
	public class GenericPanel<T> : SigmaPanel where T : UIElement
	{
		private T _content;

		public new T Content
		{
			get { return _content; }
			set
			{
				_content = value;
				base.Content = _content;
			}
		}

		public GenericPanel(string title) : base(title) { }

		public GenericPanel(string title, T content) : base(title)
		{
			Content = content;
		}
	}
}