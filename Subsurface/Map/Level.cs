﻿using FarseerPhysics;
using FarseerPhysics.Common;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Factories;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Voronoi2;

namespace Subsurface
{
    class Level
    {
        public static Level Loaded
        {
            get { return loaded; }
        }

        static Level loaded;

        private string seed;

        private int siteInterval;

        const int gridCellWidth = 2000;
        List<VoronoiCell>[,] cellGrid;

        //List<Body> bodies;
        List<VoronoiCell> cells;

        BasicEffect basicEffect;

        private VertexPositionColor[] vertices;
        private VertexBuffer vertexBuffer;

        private Vector2 startPosition;
        private Vector2 endPosition;

        Rectangle borders;

        public Vector2 StartPosition
        {
            get { return startPosition; }
        }

        public Vector2 Position
        {
            get { return ConvertUnits.ToDisplayUnits(cells[0].body.Position); }
        }

        public Level(string seed, int width, int height, int siteInterval)
        {
            this.seed = seed;

            this.siteInterval = siteInterval;

            borders = new Rectangle(0, 0, width, height);
        }

        public static Level CreateRandom(string seed = "")
        {
            if (seed == "")
            {
                seed = Rand.Range(0, int.MaxValue, false).ToString();
            }
           return new Level((string)seed, 100000, 40000, 2000);        
        }

        public void Generate(float minWidth)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();

            //Game1.random = new Random(ToolBox.SeedToInt(seed));

            if (loaded != null)
            {
                loaded.Unload();                
            }

            loaded = this;

            Voronoi voronoi = new Voronoi(1.0);

            List<Vector2> sites = new List<Vector2>();
            Random rand = new Random(ToolBox.SeedToInt(seed));

            float siteVariance = siteInterval * 0.8f;
            for (int x = siteInterval/2; x < borders.Width; x += siteInterval)
            {
                for (int y = siteInterval / 2; y < borders.Height; y += siteInterval)
                {
                    sites.Add(new Vector2(
                        x + (float)(rand.NextDouble() - 0.5) * siteVariance,
                        y + (float)(rand.NextDouble() - 0.5) * siteVariance));
                }
            }

            Stopwatch sw2 = new Stopwatch();
            sw2.Start();

            List<GraphEdge> graphEdges = voronoi.MakeVoronoiGraph(sites, borders.Width, borders.Height);


            Debug.WriteLine("MakeVoronoiGraph: " + sw2.ElapsedMilliseconds + " ms");
            sw2.Restart();

            cellGrid = new List<VoronoiCell>[borders.Width / gridCellWidth, borders.Height / gridCellWidth];
            for (int x = 0; x < borders.Width / gridCellWidth; x++)
            {
                for (int y = 0; y < borders.Height / gridCellWidth; y++)
                {
                    cellGrid[x, y] = new List<VoronoiCell>();
                }
            }

            //construct voronoi cells based on the graph edges
            cells = new List<VoronoiCell>();
            foreach (GraphEdge ge in graphEdges)
            {
                for (int i = 0; i < 2; i++)
                {
                    Site site = (i == 0) ? ge.site1 : ge.site2;

                    VoronoiCell cell = cellGrid[
                        (int)Math.Floor(site.coord.x / gridCellWidth), 
                        (int)Math.Floor(site.coord.y / gridCellWidth)].Find(c => c.site == site);

                    if (cell == null)
                    {
                        cell = new VoronoiCell(site);
                        cellGrid[(int)Math.Floor(cell.Center.X / gridCellWidth), (int)Math.Floor(cell.Center.Y / gridCellWidth)].Add(cell);
                        cells.Add(cell);
                    }

                    if (ge.cell1 == null)
                    {
                        ge.cell1 = cell;
                    }
                    else
                    {
                        ge.cell2 = cell;
                    }
                    cell.edges.Add(ge);
                }
            }
            
            Debug.WriteLine("find cells: " + sw2.ElapsedMilliseconds + " ms");
            sw2.Restart();

            //generate a path from the left edge of the map to right edge
            Rectangle pathBorders = new Rectangle(
                borders.X + (int)minWidth, borders.Y + (int)minWidth,
                borders.Right - (int)minWidth, borders.Y + borders.Height - (int)minWidth);

            List<VoronoiCell> pathCells = GeneratePath(rand,
                new Vector2((int)minWidth, rand.Next((int)minWidth, borders.Height - (int)minWidth)),
                new Vector2(borders.Width - (int)minWidth, rand.Next((int)minWidth, borders.Height - (int)minWidth)),
                cells, pathBorders, minWidth);


            //generate a couple of random paths
            for (int i = 0; i < rand.Next() % 3; i++ )
            {
                pathBorders = new Rectangle(
                borders.X + siteInterval * 2, borders.Y - siteInterval * 2,
                borders.Right - siteInterval * 2, borders.Y + borders.Height - siteInterval * 2);

                Vector2 start = pathCells[rand.Next(1,pathCells.Count-2)].Center;

                float x = pathBorders.X + (float)rand.NextDouble() * (pathBorders.Right - pathBorders.X);
                float y = pathBorders.Y + (float)rand.NextDouble() * (pathBorders.Bottom - pathBorders.Y);
                Vector2 end = new Vector2(x,y);
                
                pathCells.AddRange
                (
                    GeneratePath(rand, start,end, cells, pathBorders, 0.0f, 0.8f)
                );
            }

            Debug.WriteLine("path: " + sw2.ElapsedMilliseconds + " ms");
            sw2.Restart();

            startPosition = pathCells[0].Center;
            endPosition = pathCells[pathCells.Count - 1].Center;

            cells = CleanCells(pathCells);
            
            foreach (VoronoiCell cell in pathCells)
            {
                cells.Remove(cell);
            }

            for (int x = 0; x < cellGrid.GetLength(0); x++ )
            {
                for (int y = 0; y < cellGrid.GetLength(1); y++ )
                {
                    cellGrid[x, y].Clear();
                }
            }

            foreach (VoronoiCell cell in cells)
            {
                cellGrid[(int)Math.Floor(cell.Center.X / gridCellWidth), (int)Math.Floor(cell.Center.Y / gridCellWidth)].Add(cell);
            }

            GeneratePolygons(cells, pathCells);

            foreach (VoronoiCell cell in cells)
            {
                foreach (GraphEdge edge in cell.edges)
                {
                    edge.cell1 = null;
                    edge.cell2 = null;
                    edge.site1 = null;
                    edge.site2 = null;
                }

            }

            Debug.WriteLine("Generatelevel: " + sw2.ElapsedMilliseconds + " ms");
            sw2.Restart();

            vertexBuffer = new VertexBuffer(Game1.CurrGraphicsDevice, VertexPositionColor.VertexDeclaration, vertices.Length, BufferUsage.WriteOnly);

            vertexBuffer.SetData(vertices);

            basicEffect = new BasicEffect(Game1.CurrGraphicsDevice);
            basicEffect.VertexColorEnabled = true;

            Debug.WriteLine("Generated a map with "+sites.Count+" sites in "+sw.ElapsedMilliseconds+" ms");
        }

        private List<VoronoiCell> GeneratePath(Random rand, Vector2 start, Vector2 end, List<VoronoiCell> cells, Microsoft.Xna.Framework.Rectangle limits, float minWidth, float wanderAmount = 0.3f)
        {

            Stopwatch sw2 = new Stopwatch();
            sw2.Start();
            
            //how heavily the path "steers" towards the endpoint
            //lower values will cause the path to "wander" more, higher will make it head straight to the end
            wanderAmount = MathHelper.Clamp(wanderAmount, 0.0f, 1.0f);

            List<VoronoiCell> pathCells = new List<VoronoiCell>();

            VoronoiCell currentCell = cells[FindCellIndex(start)];
            pathCells.Add(currentCell);

            VoronoiCell endCell = cells[FindCellIndex(end)];

            do
            {
                int edgeIndex = 0;

                //steer towards target
                if (rand.NextDouble()>wanderAmount)
                {
                    for (int i = 0; i < currentCell.edges.Count; i++)
                    {
                        if (!IsIntersecting(currentCell.Center, end, currentCell.edges[i].point1, currentCell.edges[i].point2)) continue;
                        edgeIndex = i;
                        break;
                    }
                }
                //choose random edge (ignoring ones where the adjacent cell is outside limits)
                else
                {
                    List<GraphEdge> allowedEdges = new List<GraphEdge>();
                    
                    foreach(GraphEdge edge in currentCell.edges)
                    {
                        if (!limits.Contains(edge.AdjacentCell(currentCell).Center)) continue;
                    
                        allowedEdges.Add(edge);
                    }
                    edgeIndex = (allowedEdges.Count==0) ?
                        0 : currentCell.edges.IndexOf(allowedEdges[rand.Next() % allowedEdges.Count]);
                }

                currentCell = currentCell.edges[edgeIndex].AdjacentCell(currentCell);


                pathCells.Add(currentCell);

            } while (currentCell!=endCell);

            Debug.WriteLine("genpath: " + sw2.ElapsedMilliseconds + " ms");
            sw2.Restart();

            List<VoronoiCell> removedCells = GetTooCloseCells(pathCells, minWidth);
            foreach (VoronoiCell removedCell in removedCells)
            {
                if (pathCells.Contains(removedCell)) continue;
                pathCells.Add(removedCell);
            }

            Debug.WriteLine("gettooclose: " + sw2.ElapsedMilliseconds + " ms");
            sw2.Restart();

            return pathCells;
        }

        private List<VoronoiCell> GetTooCloseCells(List<VoronoiCell> emptyCells, float minDistance)
        {
            List<VoronoiCell> tooCloseCells = new List<VoronoiCell>();
            
            Vector2 position = emptyCells[0].Center;

            if (minDistance == 0.0f) return tooCloseCells;

            float step = 100.0f;

            int targetCellIndex = 1;

            minDistance *= 0.5f;
            do
            {
                for (int x = -1; x<=1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        if (x == 0 && y == 0) continue;
                        Vector2 cornerPos = position + new Vector2(x*minDistance, y*minDistance);
                        
                        int cellIndex = FindCellIndex(cornerPos);
                        if (cellIndex == -1) continue;
                        if (!tooCloseCells.Contains(cells[cellIndex])) 
                        {
                            tooCloseCells.Add(cells[cellIndex]);               
                        }
                    }
                }

                position += Vector2.Normalize(emptyCells[targetCellIndex].Center - position) * step;

                if (Vector2.Distance(emptyCells[targetCellIndex].Center, position) < step * 2.0f) targetCellIndex++;

            } while (Vector2.Distance(position, emptyCells[emptyCells.Count - 1].Center) > step*2.0f);

            return tooCloseCells;        
        }

        /// <summary>
        /// remove all cells except those that are adjacent to the empty cells
        /// </summary>
        private List<VoronoiCell> CleanCells(List<VoronoiCell> emptyCells)
        {
            List<VoronoiCell> newCells = new List<VoronoiCell>();

            foreach (VoronoiCell cell in emptyCells)
            {
                foreach (GraphEdge edge in cell.edges)
                {
                    VoronoiCell adjacent = edge.AdjacentCell(cell);
                    if (!newCells.Contains(adjacent)) newCells.Add(adjacent);
                }
            }

            return newCells;
        }

        /// <summary>
        /// check whether line from a to b is intersecting with line from c to b
        /// </summary>
        bool IsIntersecting(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float denominator = ((b.X - a.X) * (d.Y - c.Y)) - ((b.Y - a.Y) * (d.X - c.X));
            float numerator1 = ((a.Y - c.Y) * (d.X - c.X)) - ((a.X - c.X) * (d.Y - c.Y));
            float numerator2 = ((a.Y - c.Y) * (b.X - a.X)) - ((a.X - c.X) * (b.Y - a.Y));

            if (denominator == 0) return numerator1 == 0 && numerator2 == 0;

            float r = numerator1 / denominator;
            float s = numerator2 / denominator;

            return (r >= 0 && r <= 1) && (s >= 0 && s <= 1);
        }

        //public Microsoft.Xna.Framework.Point GridCell(Vector2 position)
        //{
        //    Microsoft.Xna.Framework.Point point = new Microsoft.Xna.Framework.Point(
        //        (int)Math.Floor(position.X / gridCellWidth),
        //        (int)Math.Floor(position.Y / gridCellWidth));

        //    point.X = MathHelper.Clamp(point.X, 0, cellGrid.GetLength(0) - 1);
        //    point.Y = MathHelper.Clamp(point.X, 0, cellGrid.GetLength(1) - 1);

        //    return point;
        //}
        
        /// <summary>
        /// find the index of the cell which the point is inside
        /// (actually finds the cell whose center is closest, but it's always the correct cell assuming the point is inside the borders of the diagram)
        /// </summary>
        private int FindCellIndex(Vector2 position)
        {
            float closestDist = 0.0f;
            VoronoiCell closestCell = null;

            int gridPosX = (int)Math.Floor(position.X / gridCellWidth);
            int gridPosY = (int)Math.Floor(position.Y / gridCellWidth);

            int searchOffset = 1;

            for (int x = Math.Max(gridPosX-searchOffset,0); x<=Math.Min(gridPosX+searchOffset, cellGrid.GetLength(0)-1); x++)
            {
                for (int y = Math.Max(gridPosY-searchOffset,0); y<=Math.Min(gridPosY+searchOffset, cellGrid.GetLength(1)-1); y++)
                {
                    for (int i = 0; i < cellGrid[x,y].Count; i++)
                    {
                        float dist = Vector2.Distance(cellGrid[x, y][i].Center, position);
                        if (closestDist != 0.0f && dist > closestDist) continue;

                        closestDist = dist;
                        closestCell = cellGrid[x, y][i];
                    }
                }
            }



            return cells.IndexOf(closestCell);            
        }

        private void GeneratePolygons(List<VoronoiCell> cells, List<VoronoiCell> emptyCells)
        {
            List<VertexPositionColor> verticeList = new List<VertexPositionColor>();
            //bodies = new List<Body>();

            List<Vector2> tempVertices = new List<Vector2>();
            List<Vector2> bodyPoints = new List<Vector2>();

            int n = 0;
            foreach (VoronoiCell cell in cells)
            {
                n = (n + 30) % 255;

                bodyPoints.Clear();
                tempVertices.Clear();
                foreach (GraphEdge ge in cell.edges)
                {
                    if (ge.point1 == ge.point2) continue;
                    if (!tempVertices.Contains(ge.point1)) tempVertices.Add(ge.point1);
                    if (!tempVertices.Contains(ge.point2)) tempVertices.Add(ge.point2);

                    VoronoiCell adjacentCell = ge.AdjacentCell(cell);
                    if (!emptyCells.Contains(adjacentCell)) continue;

                    ge.isSolid = true;

                    if (!bodyPoints.Contains(ge.point1)) bodyPoints.Add(ge.point1);
                    if (!bodyPoints.Contains(ge.point2)) bodyPoints.Add(ge.point2);
                }

                if (tempVertices.Count < 3) continue;
                
                int triangleCount = tempVertices.Count - 2;

                tempVertices.Sort(new CompareCCW(cell.Center));

                int lastIndex = 1;
                for (int i = 0; i < triangleCount; i++ )
                {
                    //simple triangulation
                    List<Vector2> triangleVertices = new List<Vector2>();                    
                    triangleVertices.Add(tempVertices[0]);
                    for (int j = lastIndex; j<=lastIndex+1; j++)
                    {
                        triangleVertices.Add(tempVertices[j]);
                    }
                    lastIndex += 1;
                    
                    foreach (Vector2 vertex in triangleVertices)
                    {
                        verticeList.Add(new VertexPositionColor(new Vector3(vertex, 0.0f), Color.LightGray*0.8f));//new Color(n,(n*2)%255,(n*3)%255)*0.5f));
                    }

                    //bool isSame = false;
                    //if (triangleVertices[0].Y == triangleVertices[1].Y && triangleVertices[1].Y == triangleVertices[2].Y) isSame = true;
                    //if (triangleVertices[0].X == triangleVertices[1].X && triangleVertices[1].X == triangleVertices[2].X) isSame = true;

                    //if (isSame) continue;

                    //CreateBody(cell, triangleVertices);
                }

                if (bodyPoints.Count < 2) continue;


                //todo: make sure the first point is the one where the edge should start from
                bodyPoints.Sort(new CompareCCW(cell.Center));

                if (bodyPoints.Count == tempVertices.Count)
                {

                }

                for (int i = 0; i < bodyPoints.Count; i++)
                {
                    cell.bodyVertices.Add(bodyPoints[i]);
                    bodyPoints[i] = ConvertUnits.ToSimUnits(bodyPoints[i]);
                }
                
                Vertices bodyVertices = new Vertices(bodyPoints);

                Body edgeBody = BodyFactory.CreateLoopShape(Game1.World, bodyVertices);

                //Body edgeBody = (bodyVertices.Count == tempVertices.Count) ?
                //    BodyFactory.CreateLoopShape(Game1.world, bodyVertices) : 
                //    BodyFactory.CreateChainShape(Game1.world, bodyVertices);
                        
                edgeBody.UserData = cell;

                edgeBody.BodyType = BodyType.Kinematic;
                edgeBody.CollisionCategories = Physics.CollisionWall | Physics.CollisionLevel;

                cell.body = edgeBody;
            }

            vertices = verticeList.ToArray();

            //return bodies;
        }



        public void SetPosition(Vector2 pos)
        {
            Vector2 amount = ConvertUnits.ToSimUnits(pos - Position);
            foreach (VoronoiCell cell in cells)
            {
                if (cell.body == null) continue;
                cell.body.SleepingAllowed = false;
                cell.body.SetTransform(cell.body.Position + amount, cell.body.Rotation);
            }
        }

        Vector2 prevVelocity;
        public void Move(Vector2 amount)
        {
            //position += amount;

            Vector2 velocity = amount;
            Vector2 simVelocity = ConvertUnits.ToSimUnits(amount / (float)Physics.step); 

            //DebugCheckPos();

            foreach (VoronoiCell cell in cells)
            {
                if (cell.body == null) continue;
                cell.body.LinearVelocity = simVelocity;
            }

            foreach (Character character in Character.CharacterList)
            {
                foreach (Limb limb in character.AnimController.limbs)
                {
                    //limb.body.SetTransform(limb.body.Position + amount * (float)Physics.step, limb.body.Rotation);
                    if (character.AnimController.CurrentHull == null)
                    {
                        limb.body.LinearVelocity += simVelocity;
                    }
                    else
                    {
                        if (limb.type == LimbType.LeftFoot || limb.type == LimbType.RightFoot) continue;
                        limb.body.ApplyForce((simVelocity - prevVelocity) * 10.0f * limb.Mass);
                    }
                }                
            }

            foreach (Item item in Item.itemList)
            {
                if (item.CurrentHull != null) continue;
                if (item.body == null)
                {
                    item.Move(velocity);
                }
                else
                {
                    item.body.LinearVelocity += simVelocity;
                }
            }

            prevVelocity = simVelocity;
        }

        public static void AfterWorldStep()
        {
            if (loaded == null) return;

            loaded.ResetBodyVelocities();
        }

        private void ResetBodyVelocities()
        {
            foreach (Character character in Character.CharacterList)
            {
                if (character.AnimController.CurrentHull != null) continue;
                
                foreach (Limb limb in character.AnimController.limbs)
                {
                    limb.body.LinearVelocity -= prevVelocity;
                }                
            }

            foreach (Item item in Item.itemList)
            {
                if (item.body == null || item.CurrentHull != null) continue;
                item.body.LinearVelocity -= prevVelocity;
            }
        }



        public void DebugCheckPos()
        {

            Vector2 avgPos = Vector2.Zero;
            foreach (VoronoiCell cell in cells)
            {
                if (cell.body == null) continue;


                System.Diagnostics.Debug.WriteLine(cell.body.Position);
                avgPos += cell.body.Position;
            }

            System.Diagnostics.Debug.WriteLine("avgpos: "+avgPos / cells.Count);

            System.Diagnostics.Debug.WriteLine("pos: " + Position);
        }

        Vector2 observerPosition;
        public void SetObserverPosition(Vector2 position)
        {
            observerPosition = position - this.Position;
            int gridPosX = (int)Math.Floor(observerPosition.X / gridCellWidth);
            int gridPosY = (int)Math.Floor(observerPosition.Y / gridCellWidth);
            int searchOffset = 2;

            int startX = Math.Max(gridPosX - searchOffset, 0);
            int endX = Math.Min(gridPosX + searchOffset, cellGrid.GetLength(0) - 1);

            int startY = Math.Max(gridPosY - searchOffset, 0);
            int endY = Math.Min(gridPosY + searchOffset, cellGrid.GetLength(1) - 1);

            for (int x = 0; x < cellGrid.GetLength(0); x++)
            {
                for (int y = 0; y < cellGrid.GetLength(1); y++)
                {
                    for (int i = 0; i < cellGrid[x, y].Count; i++)
                    {
                        //foreach (Body b in cellGrid[x, y][i].bodies)
                        //{
                        if (cellGrid[x, y][i].body == null) continue;
                        cellGrid[x, y][i].body.Enabled = true;// (x >= startX && x <= endX && y >= startY && y <= endY);
                        //}
                    }
                }
            }
        }


        public void RenderLines(SpriteBatch spriteBatch)
        {
            //GUI.DrawRectangle(spriteBatch, new Rectangle(borders.X, borders.Y-borders.Height, borders.Width, borders.Height), Color.Cyan);

            //for (int x = 0; x < cellGrid.GetLength(0); x++)
            //{
            //    for (int y = 0; y < cellGrid.GetLength(1); y++)
            //    {
            //        GUI.DrawRectangle(spriteBatch,
            //            new Rectangle(x * gridCellWidth + (int)position.X, borders.Y - borders.Height + y * gridCellWidth - (int)position.Y, gridCellWidth, gridCellWidth),
            //            Color.Cyan);
            //    }
            //}

            //int gridPosX = (int)Math.Floor(-observerPosition.X / gridCellWidth);
            //int gridPosY = (int)Math.Floor(-observerPosition.Y / gridCellWidth);
            //int searchOffset = 2;

            //int startX = Math.Max(gridPosX - searchOffset, 0);
            //int endX = Math.Min(gridPosX + searchOffset, cellGrid.GetLength(0) - 1);

            //int startY = Math.Max(gridPosY - searchOffset, 0);
            //int endY = Math.Min(gridPosY + searchOffset, cellGrid.GetLength(1) - 1);

            //for (int x = startX; x < endX; x++)
            //{
            //    for (int y = startY; y < endY; y++)
            //    {
            //        GUI.DrawRectangle(spriteBatch,
            //            new Rectangle(x * gridCellWidth + (int)position.X, borders.Y - borders.Height + y * gridCellWidth - (int)position.Y, gridCellWidth, gridCellWidth),
            //            Color.Cyan);
            //    }
            //}

            List<Vector2[]> edges = GetCellEdges(observerPosition, 1, false);

            //for (int i = 0; i < edges.Count; i++)
            //{
            //    GUI.DrawLine(spriteBatch, edges[i][0], edges[i][1], Color.Green);
            //}

            foreach (VoronoiCell cell in cells)
            {
                for (int i = 0; i < cell.bodyVertices.Count-1; i++)
                {
                    Vector2 start = cell.bodyVertices[i];
                    start.X += Position.X;
                    start.Y = -start.Y - Position.Y;
                    start.X += Rand.Range(-10.0f, 10.0f);

                    Vector2 end = cell.bodyVertices[i+1];
                    end.X += Position.X;
                    end.Y = -end.Y - Position.Y;
                    end.X += Rand.Range(-10.0f, 10.0f);

                    GUI.DrawLine(spriteBatch, start, end, (cell.body != null && cell.body.Enabled) ? Color.Red : Color.Red);
                }
            }
        }

        public List<Vector2[]> GetCellEdges(Vector2 refPos, int searchDepth = 2, bool onlySolid = true)
        {

            int gridPosX = (int)Math.Floor(refPos.X / gridCellWidth);
            int gridPosY = (int)Math.Floor(refPos.Y / gridCellWidth);

            int startX = Math.Max(gridPosX - searchDepth, 0);
            int endX = Math.Min(gridPosX + searchDepth, cellGrid.GetLength(0) - 1);

            int startY = Math.Max(gridPosY - searchDepth, 0);
            int endY = Math.Min(gridPosY + searchDepth, cellGrid.GetLength(1) - 1);


            List<Vector2[]> edges = new List<Vector2[]>();

            for (int x = startX; x < endX; x++)
            {
                for (int y = startY; y < endY; y++)
                {
                    foreach (VoronoiCell cell in cellGrid[x,y])
                    {
                        for (int i = 0; i < cell.edges.Count; i++)
                        {
                            if (onlySolid && !cell.edges[i].isSolid) continue;
                            Vector2 start = cell.edges[i].point1 + Position;
                            start.Y = -start.Y;

                            Vector2 end = cell.edges[i].point2 + Position;
                            end.Y = -end.Y;

                            edges.Add(new Vector2[] { start, end });
                            //GUI.DrawLine(spriteBatch, start, end, (cell.body != null && cell.body.Enabled) ? Color.Green : Color.Red);
                        }
                    }
                }
            }
            
            return edges;
        }

        public void Render(GraphicsDevice graphicsDevice, Camera cam)
        {
            if (vertices == null) return;
            if (vertices.Length <= 0) return;

            basicEffect.World = Matrix.CreateTranslation(new Vector3(Position, 0.0f))*cam.ShaderTransform
                * Matrix.CreateOrthographic(Game1.GraphicsWidth, Game1.GraphicsHeight, -1, 1) * 0.5f;


            basicEffect.CurrentTechnique.Passes[0].Apply();  
         

            graphicsDevice.DrawUserPrimitives<VertexPositionColor>(PrimitiveType.TriangleList, vertices, 0, (int)Math.Floor(vertices.Length / 3.0f));
        }

        private void Unload()
        {
            //position = Vector2.Zero;

            //foreach (VoronoiCell cell in cells)
            //{
            //    //foreach (Body b in cell.bodies)
            //    //{
            //        Game1.world.RemoveBody(cell.body);
            //    //}
            //}


            //bodies = null;

            vertices = null;

            cells = null;

            vertexBuffer.Dispose();
            vertexBuffer = null;
        }
    }
      
}