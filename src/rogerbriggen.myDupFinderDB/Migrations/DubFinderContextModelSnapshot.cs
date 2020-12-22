﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RogerBriggen.MyDupFinderDB;

namespace RogerBriggen.myDupFinderDB.Migrations
{
    [DbContext(typeof(DubFinderContext))]
    partial class DubFinderContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "5.0.1");

            modelBuilder.Entity("RogerBriggen.MyDupFinderData.ScanErrorItemDto", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("DateRunStartedUTC")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("ErrorOccurrence")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("FileCreationUTC")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("FileLastModificationUTC")
                        .HasColumnType("TEXT");

                    b.Property<long>("FileSize")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Filename")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("FilenameAndPath")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("MyException")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("OriginComputer")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("PathBase")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("ScanExecutionComputer")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("ScanName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("ScanErrorItems");
                });

            modelBuilder.Entity("RogerBriggen.MyDupFinderData.ScanItemDto", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("FileCreationUTC")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("FileLastModificationUTC")
                        .HasColumnType("TEXT");

                    b.Property<string>("FileSha512Hash")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<long>("FileSize")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Filename")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("FilenameAndPath")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("FirstScanDateUTC")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastScanDateUTC")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastSha512ScanDateUTC")
                        .HasColumnType("TEXT");

                    b.Property<string>("OriginComputer")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("PathBase")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("ScanExecutionComputer")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("ScanName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("FileSha512Hash");

                    b.HasIndex("FileSize");

                    b.HasIndex("Filename");

                    b.ToTable("ScanItems");
                });
#pragma warning restore 612, 618
        }
    }
}
