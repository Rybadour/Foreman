﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Diagnostics;

namespace Foreman
{
	public enum Direction { Up, Down, Left, Right, None }
	public struct TooltipInfo
	{
		public TooltipInfo(Point screenLocation, Point screenSize, Direction direction, String text)
		{
			ScreenLocation = screenLocation;
			ScreenSize = screenSize;
			Direction = direction;
			Text = text;
		}

		public Point ScreenLocation;
		public Point ScreenSize;
		public Direction Direction;
		public String Text;
	}

	public class FloatingTooltipControl : IDisposable
	{
		public Control Control { get; private set; }
		public Direction Direction { get; private set; }
		public Point GraphLocation { get; private set; }
		public ProductionGraphViewer GraphViewer { get; private set; }
		public event EventHandler Closing;

		public FloatingTooltipControl(Control control, Direction direction, Point graphLocation, ProductionGraphViewer parent)
		{
			Control = control;
			Direction = direction;
			GraphLocation = graphLocation;
			GraphViewer = parent;

			parent.floatingTooltipControls.Add(this);
			parent.Controls.Add(control);
			Rectangle ttRect = parent.getTooltipScreenBounds(parent.GraphToScreen(graphLocation), new Point(control.Size), direction);
			control.Location = ttRect.Location;
			control.Focus();
		}

		public void Dispose()
		{
			Control.Dispose();
			GraphViewer.floatingTooltipControls.Remove(this);
			if (Closing != null)
			{
				Closing.Invoke(this, null);
			}
		}
	}

	[Serializable]
	public partial class ProductionGraphViewer : UserControl, ISerializable
	{
		public HashSet<GraphElement> Elements = new HashSet<GraphElement>();
		public ProductionGraph Graph = new ProductionGraph();
		private List<Item> Demands = new List<Item>();
		public bool IsBeingDragged { get; private set; }
		private Point lastMouseDragPoint;
		public Point ViewOffset;
		public float ViewScale = 1f;
		private GraphElement draggedElement;
		public GraphElement DraggedElement
		{
			get { return draggedElement; }
			set { dragStartScreenPoint = Control.MousePosition; draggedElement = value; }
		}
		private Point dragStartScreenPoint;
		public Boolean clickHasBecomeDrag = false;
		public Queue<TooltipInfo> toolTipsToDraw = new Queue<TooltipInfo>();
		private Font size10Font = new Font(FontFamily.GenericSansSerif, 10);
		public bool ShowAssemblers = false;
		public bool ShowMiners = false;
		public bool ShowInserters = true;
		StringFormat stringFormat = new StringFormat();
		public GhostNodeElement GhostDragElement = null;
		public HashSet<FloatingTooltipControl> floatingTooltipControls = new HashSet<FloatingTooltipControl>();

		public Rectangle GraphBounds
		{
			get
			{
				int x = int.MaxValue;
				int y = int.MaxValue;
				foreach (NodeElement element in Elements.OfType<NodeElement>())
				{
					x = Math.Min(element.X, x);
					y = Math.Min(element.Y, y);
				}
				int width = 0;
				int height = 0;
				foreach (NodeElement element in Elements.OfType<NodeElement>())
				{
					height = Math.Max(element.Y + element.Height - y, height);
					width = Math.Max(element.X + element.Width - x, width);
				}
				return new Rectangle(x - 80, y - 80, width + 160, height + 160);
			}
		}

		public ProductionGraphViewer()
		{
			InitializeComponent();
			MouseWheel += new MouseEventHandler(ProductionGraphViewer_MouseWheel);
			DragOver += new DragEventHandler(HandleItemDragging);
			DragDrop += new DragEventHandler(HandleItemDropping);
			DragEnter += new DragEventHandler(HandleDragEntering);
			DragLeave += new EventHandler(HandleDragLeaving);
			ViewOffset = new Point(Width / -2, Height / -2);
		}

		public void UpdateNodes()
		{
			try
			{

				foreach (NodeElement node in Elements.OfType<NodeElement>().ToList())
				{
					node.Update();
				}
			} catch (OverflowException)
			{
				//Same as when working out node values, there's not really much to do here... Maybe I could show a tooltip saying the numbers are too big or something...
			}
			Invalidate();
		}

		public void AddRemoveElements()
		{
			Elements.RemoveWhere(e => e is LinkElement && !Graph.GetAllNodeLinks().Contains((e as LinkElement).DisplayedLink));
			Elements.RemoveWhere(e => e is NodeElement && !Graph.Nodes.Contains((e as NodeElement).DisplayedNode));

			foreach (ProductionNode node in Graph.Nodes)
			{
				if (!Elements.OfType<NodeElement>().Any(e => e.DisplayedNode == node))
				{
					Elements.Add(new NodeElement(node, this));
				}
			}

			foreach (NodeLink link in Graph.GetAllNodeLinks())
			{
				if (!Elements.OfType<LinkElement>().Any(e => e.DisplayedLink == link))
				{
					Elements.Add(new LinkElement(this, link));
				}
			}

			UpdateNodes();
			Invalidate();
		}

		public NodeElement GetElementForNode(ProductionNode node)
		{
			return Elements.OfType<NodeElement>().FirstOrDefault(e => e.DisplayedNode == node);
		}

		public void PositionNodes()
		{
			if (!Elements.Any())
			{
				return;
			}
			var nodeOrder = Graph.GetTopologicalSort();
			nodeOrder.Reverse();

			if (nodeOrder.Any())
			{
				List<ProductionNode>[] nodePositions = new List<ProductionNode>[nodeOrder.Count()];
				for (int i = 0; i < nodePositions.Count(); i++)
				{
					nodePositions[i] = new List<ProductionNode>();
				}

				nodePositions.First().AddRange(nodeOrder.OfType<ConsumerNode>());
				foreach (RecipeNode node in nodeOrder.OfType<RecipeNode>())
				{
					bool PositionFound = false;

					for (int i = nodePositions.Count() - 1; i >= 0 && !PositionFound; i--)
					{
						foreach (ProductionNode listNode in nodePositions[i])
						{
							if (listNode.CanUltimatelyTakeFrom(node))
							{
								nodePositions[i + 1].Add(node);
								PositionFound = true;
								break;
							}
						}
					}

					if (!PositionFound)
					{
						nodePositions.First().Add(node);
					}
				}
				nodePositions.Last().AddRange(nodeOrder.OfType<SupplyNode>());

				int marginX = 100;
				int marginY = 200;
				int y = marginY;
				int[] tierWidths = new int[nodePositions.Count()];
				for (int i = 0; i < nodePositions.Count(); i++)
				{
					var list = nodePositions[i];
					int maxHeight = 0;
					int x = marginX;

					foreach (var node in list)
					{
						NodeElement control = GetElementForNode(node);
						control.X = x;
						control.Y = y;

						x += control.Width + marginX;
						maxHeight = Math.Max(control.Height, maxHeight);
					}

					if (maxHeight > 0) // Don't add any height for empty tiers
					{
						y += maxHeight + marginY;
					}

					tierWidths[i] = x;
				}

				int centrePoint = tierWidths.Last(i => i > marginX) / 2;
				for (int i = tierWidths.Count() - 1; i >= 0; i--)
				{
					int offset = centrePoint - tierWidths[i] / 2;

					foreach (var node in nodePositions[i])
					{
						NodeElement element = GetElementForNode(node);
						element.X = element.X + offset;
					}
				}
			}

			UpdateNodes();
			LimitViewToBounds();
			Invalidate(true);
		}

		public IEnumerable<GraphElement> GetPaintingOrder()
		{
			foreach (LinkElement element in Elements.OfType<LinkElement>())
			{
				yield return element;
			}
			foreach (NodeElement element in Elements.OfType<NodeElement>())
			{
				yield return element;
			}
			foreach (DraggedLinkElement element in Elements.OfType<DraggedLinkElement>())
			{
				yield return element;
			}
			foreach (GhostNodeElement element in Elements.OfType<GhostNodeElement>())
			{
				yield return element;
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			e.Graphics.ResetTransform();
			e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
			e.Graphics.Clear(this.BackColor);
			e.Graphics.TranslateTransform(Width / 2, Height / 2);
			e.Graphics.ScaleTransform(ViewScale, ViewScale);
			e.Graphics.TranslateTransform(ViewOffset.X, ViewOffset.Y);

			Paint(e.Graphics);
		}

		public new void Paint(Graphics graphics)
		{
			foreach (GraphElement element in GetPaintingOrder())
			{
				graphics.TranslateTransform(element.X, element.Y);
				element.Paint(graphics);
				graphics.TranslateTransform(-element.X, -element.Y);
			}

			foreach (var fttp in floatingTooltipControls)
			{
				TooltipInfo ttinfo = new TooltipInfo();
				ttinfo.ScreenLocation = GraphToScreen(fttp.GraphLocation);
				ttinfo.Direction = fttp.Direction;
				ttinfo.ScreenSize = new Point(fttp.Control.Size);
				AddTooltip(ttinfo);
			}

			graphics.ResetTransform();
			while (toolTipsToDraw.Any())
			{
				var tt = toolTipsToDraw.Dequeue();

				if (tt.Text != null)
				{
					DrawTooltip(tt.ScreenLocation, tt.Text, tt.Direction, graphics);
				}
				else
				{
					DrawTooltip(tt.ScreenLocation, tt.ScreenSize, tt.Direction, graphics);
				}
			}
		}

		private void DrawTooltip(Point point, String text, Direction direction, Graphics graphics)
		{
			SizeF stringSize = graphics.MeasureString(text, size10Font);
			DrawTooltip(point, new Point((int)stringSize.Width, (int)stringSize.Height), direction, graphics, text);
		}

		private void DrawTooltip(Point screenArrowPoint, Point screenSize, Direction direction, Graphics graphics, String text = "")
		{
			int border = 2;
			int arrowSize = 10;
			Point arrowPoint1 = new Point();
			Point arrowPoint2 = new Point();

			stringFormat.LineAlignment = StringAlignment.Center;

			switch (direction)
			{
				case Direction.Down:
					arrowPoint1 = new Point(screenArrowPoint.X - arrowSize / 2, screenArrowPoint.Y - arrowSize);
					arrowPoint2 = new Point(screenArrowPoint.X + arrowSize / 2, screenArrowPoint.Y - arrowSize);
					stringFormat.Alignment = StringAlignment.Center;
					break;
				case Direction.Left:
					arrowPoint1 = new Point(screenArrowPoint.X + arrowSize, screenArrowPoint.Y - arrowSize / 2);
					arrowPoint2 = new Point(screenArrowPoint.X + arrowSize, screenArrowPoint.Y + arrowSize / 2);
					stringFormat.Alignment = StringAlignment.Near;
					break;
				case Direction.Up:
					arrowPoint1 = new Point(screenArrowPoint.X - arrowSize / 2, screenArrowPoint.Y + arrowSize);
					arrowPoint2 = new Point(screenArrowPoint.X + arrowSize / 2, screenArrowPoint.Y + arrowSize);
					stringFormat.Alignment = StringAlignment.Center;
					break;
				case Direction.Right:
					arrowPoint1 = new Point(screenArrowPoint.X - arrowSize, screenArrowPoint.Y - arrowSize / 2);
					arrowPoint2 = new Point(screenArrowPoint.X - arrowSize, screenArrowPoint.Y + arrowSize / 2);
					stringFormat.Alignment = StringAlignment.Near;
					break;
			}

			Rectangle rect = getTooltipScreenBounds(screenArrowPoint, screenSize, direction);
			Point[] points = new Point[] { screenArrowPoint, arrowPoint1, arrowPoint2 };

			if (direction == Direction.None)
			{
				rect = new Rectangle(screenArrowPoint, new Size(screenSize));
				stringFormat.Alignment = StringAlignment.Center;
			}

			graphics.FillPolygon(Brushes.DarkGray, points);
			GraphicsStuff.FillRoundRect(rect.X - border, rect.Y - border, rect.Width + border * 2, rect.Height + border * 2, 3, graphics, Brushes.DarkGray);

			Point point;
			if (stringFormat.Alignment == StringAlignment.Center)
			{
				point = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
			}
			else
			{
				point = new Point(rect.X, rect.Y + rect.Height / 2);
			}
			graphics.DrawString(text, size10Font, Brushes.White, point, stringFormat);

		}

		public Rectangle getTooltipScreenBounds(Point screenArrowPoint, Point screenSize, Direction direction)
		{
			Point centreOffset = new Point();
			int arrowSize = 10;

			switch (direction)
			{
				case Direction.Down:
					centreOffset = new Point(0, -arrowSize - screenSize.Y / 2);
					break;
				case Direction.Left:
					centreOffset = new Point(arrowSize + screenSize.X / 2, 0);
					break;
				case Direction.Up:
					centreOffset = new Point(0, arrowSize + screenSize.Y / 2);
					break;
				case Direction.Right:
					centreOffset = new Point(-arrowSize - screenSize.X / 2, 0);
					break;
			}
			int X = (screenArrowPoint.X + centreOffset.X - screenSize.X / 2);
			int Y = (screenArrowPoint.Y + centreOffset.Y - screenSize.Y / 2);
			int Width = screenSize.X;
			int Height = screenSize.Y;

			return new Rectangle(X, Y, Width, Height);
		}

		public IEnumerable<GraphElement> GetElementsAtPoint(Point point)
		{
			foreach (GraphElement element in GetPaintingOrder().Reverse<GraphElement>())
			{
				if (element.ContainsPoint(Point.Add(point, new Size(-element.X, -element.Y))))
				{
					yield return element;
				}
			}
		}

		private void ProductionGraphViewer_MouseDown(object sender, MouseEventArgs e)
		{
			ClearFloatingControls();

			Focus();
			ActiveControl = null;

			var clickedElement = GetElementsAtPoint(ScreenToGraph(e.Location)).FirstOrDefault();
			if (clickedElement != null)
			{
				clickedElement.MouseDown(Point.Add(ScreenToGraph(e.Location), new Size(-clickedElement.X, -clickedElement.Y)), e.Button);
			}

			if (e.Button == MouseButtons.Middle ||
				(e.Button == MouseButtons.Left && clickedElement == null))
			{
				IsBeingDragged = true;
				lastMouseDragPoint = new Point(e.X, e.Y);
			}
		}

		private void ProductionGraphViewer_MouseUp(object sender, MouseEventArgs e)
		{
			ClearFloatingControls();

			Focus();

			GraphElement element = GetElementsAtPoint(ScreenToGraph(e.Location)).FirstOrDefault();
			if (element != null)
			{
				element.MouseUp(Point.Add(ScreenToGraph(e.Location), new Size(-element.X, -element.Y)), e.Button);
			}

			DraggedElement = null;
			clickHasBecomeDrag = false;

			switch (e.Button)
			{
				case MouseButtons.Middle:
				case MouseButtons.Left:
					IsBeingDragged = false;
					break;
			}
		}

		private void ProductionGraphViewer_MouseMove(object sender, MouseEventArgs e)
		{
			var element = GetElementsAtPoint(ScreenToGraph(e.Location)).FirstOrDefault();
			if (element != null)
			{
				element.MouseMoved(Point.Add(ScreenToGraph(e.Location), new Size(-element.X, -element.Y)));
			}

			if (DraggedElement != null)
			{
				Point dragDiff = Point.Add(Control.MousePosition, new Size(-dragStartScreenPoint.X, -dragStartScreenPoint.Y));
				if (dragDiff.X * dragDiff.X + dragDiff.Y * dragDiff.Y > 9) //Only drag if the mouse has moved more than three pixels. This avoids dragging when the user is trying to click.
				{
					clickHasBecomeDrag = true;
					DraggedElement.Dragged(Point.Add(ScreenToGraph(e.Location), new Size(-DraggedElement.X, -DraggedElement.Y)));
				}
			}

			if ((Control.MouseButtons & MouseButtons.Middle) != 0
				|| ((Control.MouseButtons & MouseButtons.Left) != 0 && DraggedElement == null))
			{
				ViewOffset = new Point(ViewOffset.X + (int)((e.X - lastMouseDragPoint.X) / ViewScale), ViewOffset.Y + (int)((e.Y - lastMouseDragPoint.Y) / ViewScale));
				LimitViewToBounds();
				lastMouseDragPoint = e.Location;
			}

			Invalidate();
		}

		void ProductionGraphViewer_MouseWheel(object sender, MouseEventArgs e)
		{
			ClearFloatingControls();

			if (e.Delta > 0)
			{
				ViewScale *= 1.1f;
			}
			else
			{
				ViewScale /= 1.1f;
			}
			ViewScale = Math.Max(ViewScale, 0.01f);
			ViewScale = Math.Min(ViewScale, 5f);

			LimitViewToBounds();

			Invalidate();
		}

		public void ClearFloatingControls()
		{
			foreach (var control in floatingTooltipControls.ToArray())
			{
				control.Dispose();
			}
		}

		public Point DesktopToGraph(Point point)
		{
			return ScreenToGraph(PointToClient(point));
		}

		public Point DesktopToGraph(int X, int Y)
		{
			return DesktopToGraph(new Point(X, Y));
		}

		public Point ScreenToGraph(Point point)
		{
			return ScreenToGraph(point.X, point.Y);
		}

		public Point ScreenToGraph(int X, int Y)
		{
			return new Point(Convert.ToInt32(((X - Width / 2) / ViewScale) - ViewOffset.X), Convert.ToInt32(((Y - Height / 2) / ViewScale) - ViewOffset.Y));
		}

		public Point GraphToScreen(Point point)
		{
			return GraphToScreen(point.X, point.Y);
		}

		public Point GraphToScreen(int X, int Y)
		{
			return new Point(Convert.ToInt32(((X + ViewOffset.X) * ViewScale) + Width / 2), Convert.ToInt32(((Y + ViewOffset.Y) * ViewScale) + Height / 2));
		}

		//Tooltips added with this method will be drawn the next time the graph is repainted.
		public void AddTooltip(TooltipInfo info)
		{
			toolTipsToDraw.Enqueue(info);
		}

		public void DeleteNode(NodeElement node)
		{
			if (node != null)
			{
				foreach (NodeLink link in node.DisplayedNode.InputLinks.ToList().Union(node.DisplayedNode.OutputLinks.ToList()))
				{
					Elements.RemoveWhere(le => le is LinkElement && (le as LinkElement).DisplayedLink == link);
				}
				Elements.Remove(node);
				node.DisplayedNode.Destroy();
				Graph.UpdateNodeValues();
				UpdateNodes();
				Invalidate();
			}
		}

		public void LimitViewToBounds()
		{
			Rectangle bounds = GraphBounds;
			Point screenCentre = ScreenToGraph(Width / 2, Height / 2);
			if (screenCentre.X < bounds.X) { ViewOffset.X -= bounds.X - screenCentre.X; }
			if (screenCentre.Y < bounds.Y) { ViewOffset.Y -= bounds.Y - screenCentre.Y; }
			if (screenCentre.X > bounds.X + bounds.Width) { ViewOffset.X -= bounds.X + bounds.Width - screenCentre.X; }
			if (screenCentre.Y > bounds.Y + bounds.Height) { ViewOffset.Y -= bounds.Y + bounds.Height - screenCentre.Y; }
		}

		//Stolen from the designer file
		protected override void Dispose(bool disposing)
		{
			stringFormat.Dispose();
			foreach (var element in Elements.ToList())
			{
				element.Dispose();
			}

			size10Font.Dispose();

			if (disposing && (components != null))
			{
				components.Dispose();
			}
			base.Dispose(disposing);
		}

		void HandleDragEntering(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(typeof(HashSet<Item>)) || e.Data.GetDataPresent(typeof(HashSet<Recipe>)))
			{
				e.Effect = DragDropEffects.All;
			}
		}

		void HandleItemDragging(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(typeof(HashSet<Item>)))
			{
				if (GhostDragElement == null)
				{
					GhostDragElement = new GhostNodeElement(this);
					GhostDragElement.Items = e.Data.GetData(typeof(HashSet<Item>)) as HashSet<Item>;
				}

				GhostDragElement.Location = DesktopToGraph(e.X, e.Y);
			} else if (e.Data.GetDataPresent(typeof(HashSet<Recipe>)))
			{
				if (GhostDragElement == null)
				{
					GhostDragElement = new GhostNodeElement(this);
					GhostDragElement.Recipes = e.Data.GetData(typeof(HashSet<Recipe>)) as HashSet<Recipe>;
				}

				GhostDragElement.Location = DesktopToGraph(e.X, e.Y);
			}

			Invalidate();
		}

		void HandleItemDropping(object sender, DragEventArgs e)
		{
			if (GhostDragElement != null)
			{
				if (e.Data.GetDataPresent(typeof(HashSet<Item>)))
				{
					foreach (Item item in GhostDragElement.Items)
					{
						NodeElement newElement = null;

						var itemSupplyOption = new ItemChooserControl(item, "Create infinite supply node", item.FriendlyName);
						var itemOutputOption = new ItemChooserControl(item, "Create output node", item.FriendlyName);
						var itemPassthroughOption = new ItemChooserControl(item, "Create pass-through node", item.FriendlyName);

						var optionList = new List<ChooserControl>();
						optionList.Add(itemPassthroughOption);
						optionList.Add(itemOutputOption);
						foreach (Recipe recipe in DataCache.Recipes.Values.Where(r => r.Enabled))
						{
							if (recipe.Results.ContainsKey(item) && recipe.Category != "incinerator" && recipe.Category != "incineration")
							{
								optionList.Add(new RecipeChooserControl(recipe, String.Format("Create '{0}' recipe node", recipe.FriendlyName), recipe.FriendlyName));
							}
						}
						optionList.Add(itemSupplyOption);

						var chooserPanel = new ChooserPanel(optionList, this);

						Point location = GhostDragElement.Location;

						chooserPanel.Show(c =>
						{
							if (c != null)
							{
								if (c == itemSupplyOption)
								{
									newElement = new NodeElement(SupplyNode.Create(item, this.Graph), this);
								}
								else if (c is RecipeChooserControl)
								{
									newElement = new NodeElement(RecipeNode.Create((c as RecipeChooserControl).DisplayedRecipe, this.Graph), this);
								}
                                else if (c == itemPassthroughOption)
                                {
                                    newElement = new NodeElement(PassthroughNode.Create(item, this.Graph), this);
                                }
								else if (c == itemOutputOption)
								{
									newElement = new NodeElement(ConsumerNode.Create(item, this.Graph), this);
								} else
                                {
                                    Trace.Fail("No handler for selected item");
                                }

								Graph.UpdateNodeValues();
								newElement.Update();
								newElement.Location = Point.Add(location, new Size(-newElement.Width / 2, -newElement.Height / 2));
							}
						});
					}

				}
				else if (e.Data.GetDataPresent(typeof(HashSet<Recipe>)))
				{
					foreach (Recipe recipe in GhostDragElement.Recipes)
					{
						NodeElement newElement = new NodeElement(RecipeNode.Create(recipe, Graph), this);
						Graph.UpdateNodeValues();
						newElement.Update();
						newElement.Location = Point.Add(GhostDragElement.Location, new Size(-newElement.Width / 2, -newElement.Height / 2));
					}
				}

				GhostDragElement.Dispose();
			}
		}

		void HandleDragLeaving(object sender, EventArgs e)
		{
			if (GhostDragElement != null)
			{
				GhostDragElement.Dispose();
			}
		}

		public void GetObjectData(SerializationInfo info, StreamingContext context)
		{
			info.AddValue("AmountType", Graph.SelectedAmountType);
			info.AddValue("Unit", Graph.SelectedUnit);
			info.AddValue("Nodes", Graph.Nodes);
			info.AddValue("NodeLinks", Graph.GetAllNodeLinks());
			info.AddValue("EnabledAssemblers", DataCache.Assemblers.Values.Where(a => a.Enabled).Select<Assembler, String>(a => a.Name));
			info.AddValue("EnabledMiners", DataCache.Miners.Values.Where(m => m.Enabled).Select<Miner, String>(m => m.Name));
			info.AddValue("EnabledModules", DataCache.Modules.Values.Where(m => m.Enabled).Select<Module, String>(m => m.Name));
			info.AddValue("EnabledMods", DataCache.Mods.Where(m => m.Enabled).Select<Mod, String>(m => m.Name));
			info.AddValue("EnabledRecipes", DataCache.Recipes.Values.Where(r => r.Enabled).Select<Recipe, String>(r => r.Name));
			List<Point> elementLocations = new List<Point>();
			foreach (ProductionNode node in Graph.Nodes)
			{
				elementLocations.Add(GetElementForNode(node).Location);
			}
			info.AddValue("ElementLocations", elementLocations);
		}

		public void LoadFromJson(JObject json)
		{
			Graph.Nodes.Clear();
			Elements.Clear();

			//Has to go first, as all other data depends on which mods are loaded
			List<String> EnabledMods = json["EnabledMods"].Select(t => (String)t).ToList();
			foreach (Mod mod in DataCache.Mods)
			{
				mod.Enabled = EnabledMods.Contains(mod.Name);
			}
			List<String> enabledMods = DataCache.Mods.Where(m => m.Enabled).Select(m => m.Name).ToList();


            using (ProgressForm form = new ProgressForm(enabledMods))
            {
                form.ShowDialog();
            }

			Graph.SelectedAmountType = (AmountType)(int)json["AmountType"];
			Graph.SelectedUnit = (RateUnit)(int)json["Unit"];

			List<JToken> nodes = json["Nodes"].ToList<JToken>();
			foreach (var node in nodes)
			{
				ProductionNode newNode = null;

				switch ((String)node["NodeType"])
				{
					case "Consumer":
						{
							String itemName = (String)node["ItemName"];
							if (DataCache.Items.ContainsKey(itemName))
							{
								Item item = DataCache.Items[itemName];
								newNode = ConsumerNode.Create(item, Graph);
							}
							else
							{
								Item missingItem = new Item(itemName);
								missingItem.IsMissingItem = true;
								newNode = ConsumerNode.Create(missingItem, Graph);
							}
							break;
						}
					case "Supply":
						{
							String itemName = (String)node["ItemName"];
							if (DataCache.Items.ContainsKey(itemName))
							{
								Item item = DataCache.Items[itemName];
								newNode = SupplyNode.Create(item, Graph);
							}
							else
							{
								Item missingItem = new Item(itemName);
								missingItem.IsMissingItem = true;
								DataCache.Items.Add(itemName, missingItem);
								newNode = SupplyNode.Create(missingItem, Graph);
							}
							break;
						}
					case "PassThrough":
						{
							String itemName = (String)node["ItemName"];
							if (DataCache.Items.ContainsKey(itemName))
							{
								Item item = DataCache.Items[itemName];
								newNode = PassthroughNode.Create(item, Graph);
							}
							else
							{
								Item missingItem = new Item(itemName);
								missingItem.IsMissingItem = true;
								DataCache.Items.Add(itemName, missingItem);
								newNode = PassthroughNode.Create(missingItem, Graph);
							}
							break;
						}
					case "Recipe":
						{
							String recipeName = (String)node["RecipeName"];
							if (DataCache.Recipes.ContainsKey(recipeName))
							{
								Recipe recipe = DataCache.Recipes[recipeName];
								newNode = RecipeNode.Create(recipe, Graph);
							}
							else
							{
								Recipe missingRecipe = new Recipe(recipeName, 0f, new Dictionary<Item, float>(), new Dictionary<Item, float>());
								missingRecipe.IsMissingRecipe = true;
								DataCache.Recipes.Add(recipeName, missingRecipe);
								newNode = RecipeNode.Create(missingRecipe, Graph);
							}

							if (node["Assembler"] != null)
							{
								var assemblerKey = (String)node["Assembler"];
								if (DataCache.Assemblers.ContainsKey(assemblerKey))
								{
									(newNode as RecipeNode).Assembler = DataCache.Assemblers[assemblerKey];
								}
							}

							(newNode as RecipeNode).NodeModules = ModuleSelector.Load(node);
							break;
						}
                    default:
                        {
                            Trace.Fail("Unknown node type: " + node["NodeType"]);
                            break;
                        }
				}

				if (newNode != null)
				{
					newNode.rateType = (RateType)(int)node["RateType"];
                    if (newNode.rateType == RateType.Manual)
                    {
                        if (node["DesiredRate"] != null)
                        {
                            newNode.desiredRate = (float)node["DesiredRate"];
                        } else
                        {
                            // Legacy data format stored desired rate in actual
                            newNode.desiredRate = (float)node["ActualRate"];
                        }
                    }
                    if (node["SpeedBonus"] != null)
                        newNode.SpeedBonus = Math.Round((float)node["SpeedBonus"], 4);
                    if (node["ProductivityBonus"] != null)
                        newNode.ProductivityBonus = Math.Round((float)node["ProductivityBonus"], 4);
				}
			}

			List<JToken> nodeLinks = json["NodeLinks"].ToList<JToken>();
			foreach (var nodelink in nodeLinks)
			{
				ProductionNode supplier = Graph.Nodes[(int)nodelink["Supplier"]];
				ProductionNode consumer = Graph.Nodes[(int)nodelink["Consumer"]];

				String itemName = (String)nodelink["Item"];
				if (!DataCache.Items.ContainsKey(itemName))
				{
					Item missingItem = new Item(itemName);
					missingItem.IsMissingItem = true;
					DataCache.Items.Add(itemName, missingItem);
				}
				Item item = DataCache.Items[itemName];
				NodeLink.Create(supplier, consumer, item);
			}

			IEnumerable<String> EnabledAssemblers = json["EnabledAssemblers"].Select(t => (String)t);
			foreach (Assembler assembler in DataCache.Assemblers.Values)
			{
				assembler.Enabled = EnabledAssemblers.Contains(assembler.Name);
			}

			IEnumerable<String> EnabledMiners = json["EnabledMiners"].Select(t => (String)t);
			foreach (Miner miner in DataCache.Miners.Values)
			{
				miner.Enabled = EnabledMiners.Contains(miner.Name);
			}

			IEnumerable<String> EnabledModules = json["EnabledModules"].Select(t => (String)t);
			foreach (Module module in DataCache.Modules.Values)
			{
				module.Enabled = EnabledModules.Contains(module.Name);
			}

			JToken enabledRecipesToken;
			if (json.TryGetValue("EnabledRecipes", out enabledRecipesToken))
			{
				IEnumerable<String> EnabledRecipes = enabledRecipesToken.Select(t => (String)t);
				foreach (Recipe recipe in DataCache.Recipes.Values)
				{
					recipe.Enabled = EnabledRecipes.Contains(recipe.Name);
				}
			}

			Graph.UpdateNodeValues();
			AddRemoveElements();

			List<String> ElementLocations = json["ElementLocations"].Select(l => (String)l).ToList();
			for (int i = 0; i < ElementLocations.Count; i++)
			{
				int[] splitPoint = ElementLocations[i].Split(',').Select(s => Convert.ToInt32(s)).ToArray();
				GraphElement element = GetElementForNode(Graph.Nodes[i]);
				element.Location = new Point(splitPoint[0], splitPoint[1]);
			}

			LimitViewToBounds();
		}
	}
}